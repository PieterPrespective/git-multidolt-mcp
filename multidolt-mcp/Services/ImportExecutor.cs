using System.Text.Json;
using Microsoft.Extensions.Logging;
using DMMS.Models;
using DMMS.Utilities;

namespace DMMS.Services
{
    /// <summary>
    /// Service implementation for executing import operations with conflict resolution.
    /// Imports documents from external ChromaDB databases into the local DMMS-managed database,
    /// using IChromaDbService.AddDocumentsAsync for proper chunking, metadata, and batch operations.
    /// </summary>
    public class ImportExecutor : IImportExecutor
    {
        private readonly IExternalChromaDbReader _externalReader;
        private readonly IChromaDbService _chromaService;
        private readonly IImportAnalyzer _importAnalyzer;
        private readonly ILogger<ImportExecutor> _logger;

        /// <summary>
        /// Initializes a new instance of the ImportExecutor class
        /// </summary>
        /// <param name="externalReader">Service for reading external ChromaDB databases</param>
        /// <param name="chromaService">Service for local ChromaDB operations</param>
        /// <param name="importAnalyzer">Service for analyzing imports and detecting conflicts</param>
        /// <param name="logger">Logger for diagnostic information</param>
        public ImportExecutor(
            IExternalChromaDbReader externalReader,
            IChromaDbService chromaService,
            IImportAnalyzer importAnalyzer,
            ILogger<ImportExecutor> logger)
        {
            _externalReader = externalReader;
            _chromaService = chromaService;
            _importAnalyzer = importAnalyzer;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<ImportExecutionResult> ExecuteImportAsync(
            string sourcePath,
            ImportFilter? filter = null,
            List<ImportConflictResolution>? resolutions = null,
            bool autoResolveRemaining = true,
            string defaultStrategy = "keep_source")
        {
            _logger.LogInformation("Executing import from {Path} with strategy {Strategy}", sourcePath, defaultStrategy);

            try
            {
                // Step 1: Analyze import to get current state and conflicts
                var preview = await _importAnalyzer.AnalyzeImportAsync(sourcePath, filter, includeContentPreview: false);
                if (!preview.Success)
                {
                    return new ImportExecutionResult
                    {
                        Success = false,
                        ErrorMessage = preview.ErrorMessage,
                        SourcePath = sourcePath
                    };
                }

                _logger.LogDebug("Preview complete: {ToAdd} to add, {ToUpdate} to update, {Conflicts} conflicts",
                    preview.Preview?.DocumentsToAdd, preview.Preview?.DocumentsToUpdate, preview.TotalConflicts);

                // Step 2: Build resolution map from explicit resolutions
                var resolutionMap = BuildResolutionMap(resolutions);

                // Step 3: Resolve collection mappings from filter
                var collectionMappings = await ResolveCollectionMappingsAsync(sourcePath, filter);

                // Step 4: Group documents by target collection for batch import
                // This supports collection consolidation (multiple sources to single target)
                var documentsByTarget = new Dictionary<string, List<ImportDocumentData>>();
                var collectionsCreated = new HashSet<string>();
                var documentsSkipped = 0;
                var documentsToUpdate = 0;
                var conflictsResolved = 0;
                var resolutionBreakdown = new Dictionary<string, int>();

                // Get existing local collections for existence check
                var localCollections = await _chromaService.ListCollectionsAsync();
                var localCollectionSet = new HashSet<string>(localCollections);

                // Process each collection mapping
                foreach (var mapping in collectionMappings)
                {
                    _logger.LogDebug("Processing mapping: {Source} -> {Target}", mapping.SourceCollection, mapping.TargetCollection);

                    // Get external documents for this source collection
                    var externalDocs = await _externalReader.GetExternalDocumentsAsync(
                        sourcePath,
                        mapping.SourceCollection,
                        mapping.DocumentPatterns);

                    // Initialize target batch if needed
                    if (!documentsByTarget.ContainsKey(mapping.TargetCollection))
                    {
                        documentsByTarget[mapping.TargetCollection] = new List<ImportDocumentData>();
                    }

                    // Get local documents if target exists for conflict checking
                    var localDocLookup = new Dictionary<string, LocalDocumentInfo>();
                    if (localCollectionSet.Contains(mapping.TargetCollection))
                    {
                        var localDocsResult = await _chromaService.GetDocumentsAsync(mapping.TargetCollection);
                        localDocLookup = ParseLocalDocuments(localDocsResult, mapping.TargetCollection);
                    }

                    // Process each external document
                    foreach (var extDoc in externalDocs)
                    {
                        // Check if this document has a conflict
                        var conflict = preview.Conflicts.FirstOrDefault(c =>
                            c.SourceCollection == mapping.SourceCollection &&
                            c.TargetCollection == mapping.TargetCollection &&
                            c.DocumentId == extDoc.DocId);

                        if (conflict != null)
                        {
                            // Determine resolution for this conflict
                            var resolution = DetermineResolution(conflict, resolutionMap, autoResolveRemaining, defaultStrategy);

                            // Track resolution
                            var resolutionKey = resolution.ResolutionType.ToString().ToLowerInvariant();
                            resolutionBreakdown[resolutionKey] = resolutionBreakdown.GetValueOrDefault(resolutionKey, 0) + 1;
                            conflictsResolved++;

                            _logger.LogDebug("Resolving conflict {Id} with strategy {Strategy}",
                                conflict.ConflictId, resolution.ResolutionType);

                            // Apply resolution
                            switch (resolution.ResolutionType)
                            {
                                case ImportResolutionType.KeepSource:
                                    // Import the external document (overwrite local)
                                    documentsByTarget[mapping.TargetCollection].Add(CreateImportDocumentData(
                                        extDoc, sourcePath, mapping.SourceCollection, isUpdate: true));
                                    documentsToUpdate++;
                                    break;

                                case ImportResolutionType.KeepTarget:
                                    // Skip - keep local version
                                    documentsSkipped++;
                                    break;

                                case ImportResolutionType.Skip:
                                    // Skip entirely
                                    documentsSkipped++;
                                    break;

                                case ImportResolutionType.Custom:
                                    // Use custom content if provided
                                    if (!string.IsNullOrEmpty(resolution.CustomContent))
                                    {
                                        documentsByTarget[mapping.TargetCollection].Add(new ImportDocumentData
                                        {
                                            DocId = extDoc.DocId,
                                            Content = resolution.CustomContent,
                                            Metadata = BuildImportMetadata(extDoc, sourcePath, mapping.SourceCollection, resolution.CustomMetadata),
                                            IsUpdate = true
                                        });
                                        documentsToUpdate++;
                                    }
                                    else
                                    {
                                        documentsSkipped++;
                                    }
                                    break;

                                case ImportResolutionType.Merge:
                                    // For merge, we'll append external content (simple merge strategy)
                                    var localDoc = localDocLookup.GetValueOrDefault(extDoc.DocId);
                                    var mergedContent = localDoc != null
                                        ? $"{localDoc.Content}\n\n--- Merged from import ---\n\n{extDoc.Content}"
                                        : extDoc.Content;
                                    documentsByTarget[mapping.TargetCollection].Add(new ImportDocumentData
                                    {
                                        DocId = extDoc.DocId,
                                        Content = mergedContent,
                                        Metadata = BuildImportMetadata(extDoc, sourcePath, mapping.SourceCollection, null),
                                        IsUpdate = true
                                    });
                                    documentsToUpdate++;
                                    break;
                            }
                        }
                        else if (localDocLookup.ContainsKey(extDoc.DocId))
                        {
                            // Document exists in local but no conflict detected - they must be identical
                            // Skip to avoid duplicates
                            documentsSkipped++;
                        }
                        else
                        {
                            // New document - add to import batch
                            documentsByTarget[mapping.TargetCollection].Add(CreateImportDocumentData(
                                extDoc, sourcePath, mapping.SourceCollection, isUpdate: false));
                        }
                    }
                }

                // Step 5: Execute batch imports per target collection
                // CRITICAL: Use IChromaDbService.AddDocumentsAsync for proper chunking and metadata
                var totalImported = 0;
                var totalUpdated = 0;

                foreach (var (targetCollection, documents) in documentsByTarget)
                {
                    if (documents.Count == 0)
                    {
                        _logger.LogDebug("No documents to import for target collection {Target}", targetCollection);
                        continue;
                    }

                    _logger.LogInformation("Batch importing {Count} documents to collection {Target}",
                        documents.Count, targetCollection);

                    // Ensure collection exists
                    if (!localCollectionSet.Contains(targetCollection))
                    {
                        await _chromaService.CreateCollectionAsync(targetCollection);
                        collectionsCreated.Add(targetCollection);
                        _logger.LogInformation("Created new collection: {Collection}", targetCollection);
                    }

                    // Separate new documents and updates
                    var newDocs = documents.Where(d => !d.IsUpdate).ToList();
                    var updateDocs = documents.Where(d => d.IsUpdate).ToList();

                    // For updates, we need to delete existing documents first (to allow re-import with new content)
                    if (updateDocs.Count > 0)
                    {
                        var updateIds = updateDocs.Select(d => d.DocId).ToList();
                        try
                        {
                            await _chromaService.DeleteDocumentsAsync(targetCollection, updateIds, expandChunks: true);
                            _logger.LogDebug("Deleted {Count} existing documents for update", updateIds.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not delete documents for update - they may not exist yet");
                        }
                    }

                    // Combine all documents for batch add
                    var allDocsToAdd = newDocs.Concat(updateDocs).ToList();
                    if (allDocsToAdd.Count > 0)
                    {
                        var ids = allDocsToAdd.Select(d => d.DocId).ToList();
                        var contents = allDocsToAdd.Select(d => d.Content).ToList();
                        var metadatas = allDocsToAdd.Select(d => d.Metadata).ToList();

                        // CRITICAL: Single batch call to AddDocumentsAsync
                        // This ensures:
                        // - Automatic chunking (512 tokens, 50 overlap)
                        // - Single embedding calculation for entire batch
                        // - Proper metadata (is_local_change=true, source_id, chunk_index, etc.)
                        var success = await _chromaService.AddDocumentsAsync(
                            targetCollection,
                            contents,
                            ids,
                            metadatas,
                            allowDuplicateIds: false,
                            markAsLocalChange: true);

                        if (success)
                        {
                            totalImported += newDocs.Count;
                            totalUpdated += updateDocs.Count;
                            _logger.LogInformation("Successfully imported {New} new and {Updated} updated documents to {Target}",
                                newDocs.Count, updateDocs.Count, targetCollection);
                        }
                        else
                        {
                            _logger.LogError("Failed to import documents to collection {Target}", targetCollection);
                            return new ImportExecutionResult
                            {
                                Success = false,
                                ErrorMessage = $"Failed to import documents to collection {targetCollection}",
                                SourcePath = sourcePath,
                                DocumentsImported = totalImported,
                                DocumentsUpdated = totalUpdated
                            };
                        }
                    }
                }

                // Build success result
                var result = new ImportExecutionResult
                {
                    Success = true,
                    SourcePath = sourcePath,
                    DocumentsImported = totalImported,
                    DocumentsUpdated = totalUpdated,
                    DocumentsSkipped = documentsSkipped,
                    CollectionsCreated = collectionsCreated.Count,
                    ConflictsResolved = conflictsResolved,
                    ResolutionBreakdown = resolutionBreakdown,
                    Message = GenerateSuccessMessage(totalImported, totalUpdated, documentsSkipped, collectionsCreated.Count, conflictsResolved)
                };

                _logger.LogInformation("Import execution complete: {Imported} imported, {Updated} updated, {Skipped} skipped, {Conflicts} conflicts resolved",
                    totalImported, totalUpdated, documentsSkipped, conflictsResolved);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import execution failed for {Path}", sourcePath);
                return new ImportExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Import execution failed: {ex.Message}",
                    SourcePath = sourcePath
                };
            }
        }

        /// <inheritdoc />
        public async Task<bool> ResolveImportConflictAsync(
            string sourcePath,
            ImportConflictInfo conflict,
            ImportConflictResolution resolution)
        {
            _logger.LogInformation("Resolving single conflict {Id} with strategy {Strategy}",
                conflict.ConflictId, resolution.ResolutionType);

            try
            {
                // Get the external document
                var externalDocs = await _externalReader.GetExternalDocumentsAsync(
                    sourcePath,
                    conflict.SourceCollection,
                    new List<string> { conflict.DocumentId });

                if (externalDocs.Count == 0)
                {
                    _logger.LogWarning("External document {DocId} not found in {Collection}",
                        conflict.DocumentId, conflict.SourceCollection);
                    return false;
                }

                var extDoc = externalDocs[0];

                // Apply resolution
                switch (resolution.ResolutionType)
                {
                    case ImportResolutionType.KeepSource:
                        // Delete existing and import external
                        try
                        {
                            await _chromaService.DeleteDocumentsAsync(
                                conflict.TargetCollection,
                                new List<string> { conflict.DocumentId },
                                expandChunks: true);
                        }
                        catch { /* May not exist */ }

                        var metadata = BuildImportMetadata(extDoc, sourcePath, conflict.SourceCollection, null);
                        await _chromaService.AddDocumentsAsync(
                            conflict.TargetCollection,
                            new List<string> { extDoc.Content },
                            new List<string> { extDoc.DocId },
                            new List<Dictionary<string, object>> { metadata },
                            markAsLocalChange: true);
                        return true;

                    case ImportResolutionType.KeepTarget:
                        // Do nothing - keep local
                        return true;

                    case ImportResolutionType.Skip:
                        // Do nothing
                        return true;

                    case ImportResolutionType.Custom:
                        if (string.IsNullOrEmpty(resolution.CustomContent))
                            return false;

                        try
                        {
                            await _chromaService.DeleteDocumentsAsync(
                                conflict.TargetCollection,
                                new List<string> { conflict.DocumentId },
                                expandChunks: true);
                        }
                        catch { /* May not exist */ }

                        var customMetadata = BuildImportMetadata(extDoc, sourcePath, conflict.SourceCollection, resolution.CustomMetadata);
                        await _chromaService.AddDocumentsAsync(
                            conflict.TargetCollection,
                            new List<string> { resolution.CustomContent },
                            new List<string> { conflict.DocumentId },
                            new List<Dictionary<string, object>> { customMetadata },
                            markAsLocalChange: true);
                        return true;

                    case ImportResolutionType.Merge:
                        // Get local document
                        var localDocsResult = await _chromaService.GetDocumentsAsync(
                            conflict.TargetCollection,
                            new List<string> { conflict.DocumentId });
                        var localDocs = ParseLocalDocuments(localDocsResult, conflict.TargetCollection);
                        var localDoc = localDocs.GetValueOrDefault(conflict.DocumentId);

                        var mergedContent = localDoc != null
                            ? $"{localDoc.Content}\n\n--- Merged from import ---\n\n{extDoc.Content}"
                            : extDoc.Content;

                        try
                        {
                            await _chromaService.DeleteDocumentsAsync(
                                conflict.TargetCollection,
                                new List<string> { conflict.DocumentId },
                                expandChunks: true);
                        }
                        catch { /* May not exist */ }

                        var mergeMetadata = BuildImportMetadata(extDoc, sourcePath, conflict.SourceCollection, null);
                        await _chromaService.AddDocumentsAsync(
                            conflict.TargetCollection,
                            new List<string> { mergedContent },
                            new List<string> { conflict.DocumentId },
                            new List<Dictionary<string, object>> { mergeMetadata },
                            markAsLocalChange: true);
                        return true;

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve conflict {Id}", conflict.ConflictId);
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<int> AutoResolveImportConflictsAsync(
            string sourcePath,
            List<ImportConflictInfo> conflicts,
            string strategy = "keep_source")
        {
            _logger.LogInformation("Auto-resolving {Count} conflicts with strategy {Strategy}",
                conflicts.Count, strategy);

            var resolved = 0;
            var resolutionType = ImportUtility.ParseResolutionType(strategy);

            foreach (var conflict in conflicts)
            {
                var resolution = new ImportConflictResolution
                {
                    ConflictId = conflict.ConflictId,
                    ResolutionType = resolutionType
                };

                if (await ResolveImportConflictAsync(sourcePath, conflict, resolution))
                {
                    resolved++;
                }
            }

            _logger.LogInformation("Auto-resolved {Resolved} of {Total} conflicts", resolved, conflicts.Count);
            return resolved;
        }

        /// <inheritdoc />
        public async Task<(bool IsValid, List<string> Errors)> ValidateResolutionsAsync(
            ImportPreviewResult preview,
            List<ImportConflictResolution> resolutions)
        {
            var errors = new List<string>();

            if (!preview.Success)
            {
                errors.Add("Preview result is invalid");
                return (false, errors);
            }

            var conflictIds = preview.Conflicts.Select(c => c.ConflictId).ToHashSet();

            foreach (var resolution in resolutions)
            {
                // Check if conflict ID exists
                if (!conflictIds.Contains(resolution.ConflictId))
                {
                    errors.Add($"Unknown conflict ID: {resolution.ConflictId}");
                }

                // Check if Custom resolution has content
                if (resolution.ResolutionType == ImportResolutionType.Custom &&
                    string.IsNullOrEmpty(resolution.CustomContent))
                {
                    errors.Add($"Custom resolution for {resolution.ConflictId} requires custom_content");
                }
            }

            return (errors.Count == 0, errors);
        }

        #region Private Helper Methods

        /// <summary>
        /// Builds a lookup map from conflict ID to resolution
        /// </summary>
        private Dictionary<string, ImportConflictResolution> BuildResolutionMap(List<ImportConflictResolution>? resolutions)
        {
            var map = new Dictionary<string, ImportConflictResolution>();
            if (resolutions == null) return map;

            foreach (var resolution in resolutions)
            {
                if (!string.IsNullOrEmpty(resolution.ConflictId))
                {
                    map[resolution.ConflictId] = resolution;
                }
            }

            return map;
        }

        /// <summary>
        /// Determines the resolution to apply for a conflict
        /// </summary>
        private ImportConflictResolution DetermineResolution(
            ImportConflictInfo conflict,
            Dictionary<string, ImportConflictResolution> resolutionMap,
            bool autoResolveRemaining,
            string defaultStrategy)
        {
            // Check for explicit resolution
            if (resolutionMap.TryGetValue(conflict.ConflictId, out var resolution))
            {
                return resolution;
            }

            // Auto-resolve if enabled
            if (autoResolveRemaining)
            {
                return new ImportConflictResolution
                {
                    ConflictId = conflict.ConflictId,
                    ResolutionType = ImportUtility.ParseResolutionType(defaultStrategy)
                };
            }

            // Default to skip if no resolution provided and auto-resolve disabled
            return new ImportConflictResolution
            {
                ConflictId = conflict.ConflictId,
                ResolutionType = ImportResolutionType.Skip
            };
        }

        /// <summary>
        /// Creates ImportDocumentData from external document
        /// </summary>
        private ImportDocumentData CreateImportDocumentData(
            ExternalDocument extDoc,
            string sourcePath,
            string sourceCollection,
            bool isUpdate)
        {
            return new ImportDocumentData
            {
                DocId = extDoc.DocId,
                Content = extDoc.Content,
                Metadata = BuildImportMetadata(extDoc, sourcePath, sourceCollection, null),
                IsUpdate = isUpdate
            };
        }

        /// <summary>
        /// Builds metadata for imported document with import tracking fields
        /// </summary>
        private Dictionary<string, object> BuildImportMetadata(
            ExternalDocument extDoc,
            string sourcePath,
            string sourceCollection,
            Dictionary<string, object>? customMetadata)
        {
            // Start with external document metadata or custom metadata
            var metadata = customMetadata != null
                ? new Dictionary<string, object>(customMetadata)
                : (extDoc.Metadata != null
                    ? new Dictionary<string, object>(extDoc.Metadata)
                    : new Dictionary<string, object>());

            // Add import tracking metadata
            metadata["import_source"] = sourcePath;
            metadata["import_source_collection"] = sourceCollection;
            metadata["import_timestamp"] = DateTime.UtcNow.ToString("O");

            // Note: is_local_change is set by AddDocumentsAsync when markAsLocalChange=true

            return metadata;
        }

        /// <summary>
        /// Resolves collection mappings from the filter, expanding wildcards
        /// </summary>
        private async Task<List<CollectionMapping>> ResolveCollectionMappingsAsync(
            string sourcePath,
            ImportFilter? filter)
        {
            var mappings = new List<CollectionMapping>();

            if (filter == null || filter.IsImportAll)
            {
                // Import all: each external collection maps to same-named local collection
                var externalCollections = await _externalReader.ListExternalCollectionsAsync(sourcePath);
                foreach (var collection in externalCollections)
                {
                    mappings.Add(new CollectionMapping
                    {
                        SourceCollection = collection.Name,
                        TargetCollection = collection.Name,
                        DocumentPatterns = null
                    });
                }
            }
            else if (filter.Collections != null)
            {
                foreach (var spec in filter.Collections)
                {
                    if (spec.HasCollectionWildcard)
                    {
                        // Expand wildcard to matching collections
                        var matchingCollections = await _externalReader.ListMatchingCollectionsAsync(
                            sourcePath, spec.Name);

                        foreach (var sourceCol in matchingCollections)
                        {
                            mappings.Add(new CollectionMapping
                            {
                                SourceCollection = sourceCol,
                                TargetCollection = spec.ImportInto,
                                DocumentPatterns = spec.Documents
                            });
                        }
                    }
                    else
                    {
                        // Exact collection name
                        mappings.Add(new CollectionMapping
                        {
                            SourceCollection = spec.Name,
                            TargetCollection = spec.ImportInto,
                            DocumentPatterns = spec.Documents
                        });
                    }
                }
            }

            return mappings;
        }

        /// <summary>
        /// Parses local documents from ChromaDB response
        /// </summary>
        private Dictionary<string, LocalDocumentInfo> ParseLocalDocuments(object? docsResult, string collectionName)
        {
            var docs = new Dictionary<string, LocalDocumentInfo>();

            if (docsResult == null) return docs;

            try
            {
                var json = JsonSerializer.Serialize(docsResult);
                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("ids", out var idsArray) &&
                    root.TryGetProperty("documents", out var docsArray))
                {
                    var ids = idsArray.EnumerateArray().ToList();
                    var documents = docsArray.EnumerateArray().ToList();
                    var metadatas = root.TryGetProperty("metadatas", out var metaArray)
                        ? metaArray.EnumerateArray().ToList()
                        : new List<JsonElement>();

                    for (int i = 0; i < ids.Count; i++)
                    {
                        var docId = ids[i].GetString() ?? string.Empty;
                        var content = i < documents.Count ? documents[i].GetString() ?? string.Empty : string.Empty;
                        var metadata = i < metadatas.Count ? ParseMetadata(metadatas[i]) : new Dictionary<string, object>();

                        docs[docId] = new LocalDocumentInfo
                        {
                            DocId = docId,
                            CollectionName = collectionName,
                            Content = content,
                            ContentHash = ImportUtility.ComputeContentHash(content),
                            Metadata = metadata
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing local documents for collection {Col}", collectionName);
            }

            return docs;
        }

        /// <summary>
        /// Parses metadata from JSON element
        /// </summary>
        private Dictionary<string, object> ParseMetadata(JsonElement element)
        {
            var metadata = new Dictionary<string, object>();

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    metadata[prop.Name] = JsonElementToObject(prop.Value);
                }
            }

            return metadata;
        }

        /// <summary>
        /// Converts JsonElement to object
        /// </summary>
        private object JsonElementToObject(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number when element.TryGetInt32(out var i) => i,
                JsonValueKind.Number when element.TryGetInt64(out var l) => l,
                JsonValueKind.Number when element.TryGetDouble(out var d) => d,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null!,
                _ => element.GetRawText()
            };
        }

        /// <summary>
        /// Generates success message for import result
        /// </summary>
        private string GenerateSuccessMessage(
            int imported,
            int updated,
            int skipped,
            int collectionsCreated,
            int conflictsResolved)
        {
            var parts = new List<string>();

            if (imported > 0)
                parts.Add($"{imported} document(s) imported");

            if (updated > 0)
                parts.Add($"{updated} document(s) updated");

            if (skipped > 0)
                parts.Add($"{skipped} document(s) skipped");

            if (collectionsCreated > 0)
                parts.Add($"{collectionsCreated} collection(s) created");

            if (conflictsResolved > 0)
                parts.Add($"{conflictsResolved} conflict(s) resolved");

            return parts.Count > 0
                ? $"Import completed: {string.Join(", ", parts)}"
                : "Import completed with no changes";
        }

        #endregion

        #region Internal Types

        /// <summary>
        /// Internal type representing a resolved collection mapping
        /// </summary>
        private record CollectionMapping
        {
            public string SourceCollection { get; init; } = string.Empty;
            public string TargetCollection { get; init; } = string.Empty;
            public List<string>? DocumentPatterns { get; init; }
        }

        /// <summary>
        /// Internal type for document data prepared for import
        /// </summary>
        private record ImportDocumentData
        {
            public string DocId { get; init; } = string.Empty;
            public string Content { get; init; } = string.Empty;
            public Dictionary<string, object> Metadata { get; init; } = new();
            public bool IsUpdate { get; init; }
        }

        /// <summary>
        /// Internal type representing a local document with parsed information
        /// </summary>
        private record LocalDocumentInfo
        {
            public string DocId { get; init; } = string.Empty;
            public string CollectionName { get; init; } = string.Empty;
            public string Content { get; init; } = string.Empty;
            public string ContentHash { get; init; } = string.Empty;
            public Dictionary<string, object>? Metadata { get; init; }
        }

        #endregion
    }
}
