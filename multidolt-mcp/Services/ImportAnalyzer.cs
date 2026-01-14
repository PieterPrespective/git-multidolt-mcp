using System.Text.Json;
using Microsoft.Extensions.Logging;
using DMMS.Models;
using DMMS.Utilities;

namespace DMMS.Services
{
    /// <summary>
    /// Service implementation for analyzing import operations and detecting conflicts.
    /// Compares documents from external ChromaDB databases with local database to identify
    /// conflicts and generate comprehensive import previews.
    /// </summary>
    public class ImportAnalyzer : IImportAnalyzer
    {
        private readonly IExternalChromaDbReader _externalReader;
        private readonly IChromaDbService _chromaService;
        private readonly ILogger<ImportAnalyzer> _logger;

        /// <summary>
        /// Initializes a new instance of the ImportAnalyzer class
        /// </summary>
        /// <param name="externalReader">Service for reading external ChromaDB databases</param>
        /// <param name="chromaService">Service for accessing local ChromaDB</param>
        /// <param name="logger">Logger for diagnostic information</param>
        public ImportAnalyzer(
            IExternalChromaDbReader externalReader,
            IChromaDbService chromaService,
            ILogger<ImportAnalyzer> logger)
        {
            _externalReader = externalReader;
            _chromaService = chromaService;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<ImportPreviewResult> AnalyzeImportAsync(
            string sourcePath,
            ImportFilter? filter = null,
            bool includeContentPreview = false)
        {
            _logger.LogInformation("Analyzing import from external database: {Path}", sourcePath);

            try
            {
                // Step 1: Validate external database
                var validation = await _externalReader.ValidateExternalDbAsync(sourcePath);
                if (!validation.IsValid)
                {
                    return new ImportPreviewResult
                    {
                        Success = false,
                        ErrorMessage = validation.ErrorMessage,
                        SourcePath = sourcePath
                    };
                }

                _logger.LogDebug("External database validated: {Collections} collections, {Docs} documents",
                    validation.CollectionCount, validation.TotalDocuments);

                // Step 2: Determine collection mappings
                var collectionMappings = await ResolveCollectionMappingsAsync(sourcePath, filter);
                _logger.LogDebug("Resolved {Count} collection mappings", collectionMappings.Count);

                // Step 3: Analyze each collection mapping for conflicts
                var allConflicts = new List<ImportConflictInfo>();
                var affectedCollections = new List<string>();
                var collectionsToCreate = new HashSet<string>();
                var collectionsToUpdate = new HashSet<string>();
                var totalDocsToAdd = 0;
                var totalDocsToUpdate = 0;
                var totalDocsToSkip = 0;

                // Get local collections once for reuse
                var localCollections = await _chromaService.ListCollectionsAsync();

                // Group by target collection for analysis
                var mappingsByTarget = collectionMappings
                    .GroupBy(m => m.TargetCollection)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var (targetCollection, mappings) in mappingsByTarget)
                {
                    // Check if target collection exists
                    var targetExists = localCollections.Contains(targetCollection);

                    if (!targetExists)
                    {
                        collectionsToCreate.Add(targetCollection);
                    }
                    else
                    {
                        collectionsToUpdate.Add(targetCollection);
                    }

                    if (!affectedCollections.Contains(targetCollection))
                    {
                        affectedCollections.Add(targetCollection);
                    }

                    // Analyze each source collection mapping to this target
                    foreach (var mapping in mappings)
                    {
                        var (conflicts, added, updated, skipped) = await AnalyzeCollectionPairAsync(
                            sourcePath,
                            mapping.SourceCollection,
                            targetCollection,
                            mapping.DocumentPatterns,
                            includeContentPreview,
                            targetExists);

                        allConflicts.AddRange(conflicts);
                        totalDocsToAdd += added;
                        totalDocsToUpdate += updated;
                        totalDocsToSkip += skipped;
                    }
                }

                // Create preview with all values at once
                var preview = new ImportChangesPreview
                {
                    DocumentsToAdd = totalDocsToAdd,
                    DocumentsToUpdate = totalDocsToUpdate,
                    DocumentsToSkip = totalDocsToSkip,
                    CollectionsToCreate = collectionsToCreate.Count,
                    CollectionsToUpdate = collectionsToUpdate.Count,
                    AffectedCollections = affectedCollections
                };

                // Calculate auto-resolvable vs manual conflicts
                var autoResolvable = allConflicts.Count(c => c.AutoResolvable);
                var manualConflicts = allConflicts.Count - autoResolvable;

                var result = new ImportPreviewResult
                {
                    Success = true,
                    SourcePath = sourcePath,
                    CanAutoImport = manualConflicts == 0,
                    TotalConflicts = allConflicts.Count,
                    AutoResolvableConflicts = autoResolvable,
                    ManualConflicts = manualConflicts,
                    Conflicts = allConflicts,
                    Preview = preview,
                    RecommendedAction = DetermineRecommendedAction(allConflicts, manualConflicts),
                    Message = GeneratePreviewMessage(allConflicts.Count, autoResolvable, manualConflicts, preview)
                };

                _logger.LogInformation("Import analysis complete: {ToAdd} to add, {ToUpdate} to update, {Conflicts} conflicts ({Auto} auto-resolvable)",
                    totalDocsToAdd, totalDocsToUpdate, allConflicts.Count, autoResolvable);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze import from {Path}", sourcePath);
                return new ImportPreviewResult
                {
                    Success = false,
                    ErrorMessage = $"Import analysis failed: {ex.Message}",
                    SourcePath = sourcePath
                };
            }
        }

        /// <inheritdoc />
        public async Task<List<ImportConflictInfo>> GetDetailedImportConflictsAsync(
            string sourcePath,
            string sourceCollection,
            string targetCollection,
            List<string>? documentIdPatterns = null)
        {
            _logger.LogDebug("Getting detailed conflicts for {Source} -> {Target}", sourceCollection, targetCollection);

            try
            {
                // Check if target exists
                var localCollections = await _chromaService.ListCollectionsAsync();
                var targetExists = localCollections.Contains(targetCollection);

                var (conflicts, _, _, _) = await AnalyzeCollectionPairAsync(
                    sourcePath,
                    sourceCollection,
                    targetCollection,
                    documentIdPatterns,
                    includeContentPreview: true,
                    targetExists);

                return conflicts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get detailed conflicts for {Source} -> {Target}",
                    sourceCollection, targetCollection);
                return new List<ImportConflictInfo>();
            }
        }

        /// <inheritdoc />
        public async Task<bool> CanAutoResolveImportConflictAsync(ImportConflictInfo conflict)
        {
            // Metadata-only conflicts are auto-resolvable
            if (conflict.Type == ImportConflictType.MetadataConflict)
            {
                _logger.LogDebug("Conflict {Id}: MetadataConflict is auto-resolvable", conflict.ConflictId);
                return true;
            }

            // Content modifications with identical content are auto-resolvable
            if (conflict.Type == ImportConflictType.ContentModification)
            {
                if (conflict.SourceContentHash == conflict.TargetContentHash)
                {
                    _logger.LogDebug("Conflict {Id}: Identical content hash - auto-resolvable", conflict.ConflictId);
                    return true;
                }
            }

            // ID collisions and content modifications with different content are NOT auto-resolvable
            _logger.LogDebug("Conflict {Id}: Type {Type} is NOT auto-resolvable", conflict.ConflictId, conflict.Type);
            return false;
        }

        /// <inheritdoc />
        public async Task<ImportChangesPreview> GetQuickPreviewAsync(
            string sourcePath,
            ImportFilter? filter = null)
        {
            _logger.LogDebug("Getting quick preview for {Path}", sourcePath);

            try
            {
                // Validate external database
                var validation = await _externalReader.ValidateExternalDbAsync(sourcePath);
                if (!validation.IsValid)
                {
                    return new ImportChangesPreview { AffectedCollections = new List<string>() };
                }

                // Resolve collection mappings
                var mappings = await ResolveCollectionMappingsAsync(sourcePath, filter);

                // Get local collections for existence check
                var localCollections = await _chromaService.ListCollectionsAsync();
                var localCollectionSet = new HashSet<string>(localCollections);

                var affectedCollections = new List<string>();
                var collectionsToCreate = new HashSet<string>();
                var collectionsToUpdate = new HashSet<string>();
                var docsToAdd = 0;
                var docsToUpdate = 0;

                foreach (var mapping in mappings)
                {
                    if (!affectedCollections.Contains(mapping.TargetCollection))
                    {
                        affectedCollections.Add(mapping.TargetCollection);
                    }

                    if (localCollectionSet.Contains(mapping.TargetCollection))
                    {
                        collectionsToUpdate.Add(mapping.TargetCollection);
                    }
                    else
                    {
                        collectionsToCreate.Add(mapping.TargetCollection);
                    }

                    // Get external document count
                    var extDocs = await _externalReader.GetExternalDocumentsAsync(
                        sourcePath,
                        mapping.SourceCollection,
                        mapping.DocumentPatterns);

                    // For quick preview, assume all new if collection doesn't exist
                    if (!localCollectionSet.Contains(mapping.TargetCollection))
                    {
                        docsToAdd += extDocs.Count;
                    }
                    else
                    {
                        // Rough estimate: assume some updates needed
                        docsToAdd += extDocs.Count / 2;
                        docsToUpdate += extDocs.Count / 2;
                    }
                }

                return new ImportChangesPreview
                {
                    DocumentsToAdd = docsToAdd,
                    DocumentsToUpdate = docsToUpdate,
                    DocumentsToSkip = 0,
                    CollectionsToCreate = collectionsToCreate.Count,
                    CollectionsToUpdate = collectionsToUpdate.Count,
                    AffectedCollections = affectedCollections
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Quick preview failed for {Path}", sourcePath);
                return new ImportChangesPreview { AffectedCollections = new List<string>() };
            }
        }

        #region Private Helper Methods

        /// <summary>
        /// Resolves collection mappings from the filter, expanding wildcards to actual collection names
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
        /// Analyzes a single source-target collection pair for conflicts
        /// </summary>
        private async Task<(List<ImportConflictInfo> conflicts, int toAdd, int toUpdate, int toSkip)> AnalyzeCollectionPairAsync(
            string sourcePath,
            string sourceCollection,
            string targetCollection,
            List<string>? documentPatterns,
            bool includeContentPreview,
            bool targetExists)
        {
            var conflicts = new List<ImportConflictInfo>();
            var toAdd = 0;
            var toUpdate = 0;
            var toSkip = 0;

            try
            {
                // Get external documents
                var externalDocs = await _externalReader.GetExternalDocumentsAsync(
                    sourcePath, sourceCollection, documentPatterns);

                _logger.LogDebug("Retrieved {Count} documents from external collection {Col}",
                    externalDocs.Count, sourceCollection);

                if (!targetExists)
                {
                    // Target doesn't exist - all documents are new
                    toAdd = externalDocs.Count;
                    return (conflicts, toAdd, toUpdate, toSkip);
                }

                // Get local documents for comparison
                var localDocsResult = await _chromaService.GetDocumentsAsync(targetCollection);
                var localDocs = ParseLocalDocuments(localDocsResult, targetCollection);

                _logger.LogDebug("Retrieved {Count} documents from local collection {Col}",
                    localDocs.Count, targetCollection);

                // Build lookup for local documents by ID
                var localDocLookup = localDocs.ToDictionary(d => d.DocId, d => d);

                // Compare each external document with local
                foreach (var extDoc in externalDocs)
                {
                    if (localDocLookup.TryGetValue(extDoc.DocId, out var localDoc))
                    {
                        // Document exists in both - check for conflicts
                        var conflict = DetectConflict(extDoc, localDoc, sourceCollection, targetCollection, includeContentPreview);
                        if (conflict != null)
                        {
                            conflicts.Add(conflict);
                            toUpdate++;
                        }
                        else if (extDoc.ContentHash == localDoc.ContentHash)
                        {
                            // Identical - skip
                            toSkip++;
                        }
                        else
                        {
                            // Different but no conflict (shouldn't happen if DetectConflict is correct)
                            toUpdate++;
                        }
                    }
                    else
                    {
                        // Document only exists in external - add
                        toAdd++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing collection pair {Source} -> {Target}",
                    sourceCollection, targetCollection);
            }

            return (conflicts, toAdd, toUpdate, toSkip);
        }

        /// <summary>
        /// Detects conflict between external and local document
        /// </summary>
        private ImportConflictInfo? DetectConflict(
            ExternalDocument extDoc,
            LocalDocumentInfo localDoc,
            string sourceCollection,
            string targetCollection,
            bool includeContentPreview)
        {
            // If content hashes are identical, no conflict
            if (extDoc.ContentHash == localDoc.ContentHash)
            {
                // Check for metadata-only differences
                if (!AreMetadatasEqual(extDoc.Metadata, localDoc.Metadata))
                {
                    var metaConflict = CreateConflictInfo(
                        extDoc, localDoc, sourceCollection, targetCollection,
                        ImportConflictType.MetadataConflict, includeContentPreview);
                    metaConflict = metaConflict with { AutoResolvable = true };
                    return metaConflict;
                }

                // Fully identical - no conflict
                return null;
            }

            // Content is different - this is a content modification conflict
            var conflict = CreateConflictInfo(
                extDoc, localDoc, sourceCollection, targetCollection,
                ImportConflictType.ContentModification, includeContentPreview);

            return conflict;
        }

        /// <summary>
        /// Creates a conflict info record with deterministic ID
        /// </summary>
        private ImportConflictInfo CreateConflictInfo(
            ExternalDocument extDoc,
            LocalDocumentInfo localDoc,
            string sourceCollection,
            string targetCollection,
            ImportConflictType conflictType,
            bool includeContentPreview)
        {
            // Generate deterministic conflict ID
            var conflictId = ImportUtility.GenerateImportConflictId(
                sourceCollection, targetCollection, extDoc.DocId, conflictType);

            _logger.LogDebug("Generated conflict ID {Id} for doc {DocId} ({Type})",
                conflictId, extDoc.DocId, conflictType);

            var autoResolvable = ImportUtility.IsAutoResolvable(conflictType);
            var suggestedResolution = ImportUtility.GetSuggestedResolution(conflictType);
            var resolutionOptions = ImportUtility.GetResolutionOptions(conflictType);

            return new ImportConflictInfo
            {
                ConflictId = conflictId,
                SourceCollection = sourceCollection,
                TargetCollection = targetCollection,
                DocumentId = extDoc.DocId,
                Type = conflictType,
                AutoResolvable = autoResolvable,
                SourceContent = includeContentPreview ? TruncateContent(extDoc.Content) : null,
                TargetContent = includeContentPreview ? TruncateContent(localDoc.Content) : null,
                SourceContentHash = extDoc.ContentHash,
                TargetContentHash = localDoc.ContentHash,
                SuggestedResolution = suggestedResolution,
                ResolutionOptions = resolutionOptions,
                SourceMetadata = extDoc.Metadata,
                TargetMetadata = localDoc.Metadata
            };
        }

        /// <summary>
        /// Parses local documents from ChromaDB response
        /// </summary>
        private List<LocalDocumentInfo> ParseLocalDocuments(object? docsResult, string collectionName)
        {
            var docs = new List<LocalDocumentInfo>();

            if (docsResult == null) return docs;

            try
            {
                var json = JsonSerializer.Serialize(docsResult);
                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                // Handle ChromaDB response format: { ids: [], documents: [], metadatas: [] }
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

                        docs.Add(new LocalDocumentInfo
                        {
                            DocId = docId,
                            CollectionName = collectionName,
                            Content = content,
                            ContentHash = ImportUtility.ComputeContentHash(content),
                            Metadata = metadata
                        });
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
        /// Compares two metadata dictionaries for equality
        /// </summary>
        private bool AreMetadatasEqual(Dictionary<string, object>? meta1, Dictionary<string, object>? meta2)
        {
            if (meta1 == null && meta2 == null) return true;
            if (meta1 == null || meta2 == null) return false;

            // Ignore internal metadata fields
            var ignoredFields = new HashSet<string> { "is_local_change", "import_source", "import_timestamp" };

            var filtered1 = meta1.Where(kvp => !ignoredFields.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var filtered2 = meta2.Where(kvp => !ignoredFields.Contains(kvp.Key)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            if (filtered1.Count != filtered2.Count) return false;

            foreach (var kvp in filtered1)
            {
                if (!filtered2.TryGetValue(kvp.Key, out var val2)) return false;

                var val1Str = kvp.Value?.ToString() ?? string.Empty;
                var val2Str = val2?.ToString() ?? string.Empty;

                if (val1Str != val2Str) return false;
            }

            return true;
        }

        /// <summary>
        /// Truncates content for preview display
        /// </summary>
        private string? TruncateContent(string? content, int maxLength = 500)
        {
            if (string.IsNullOrEmpty(content)) return content;
            if (content.Length <= maxLength) return content;
            return content.Substring(0, maxLength) + "...";
        }

        /// <summary>
        /// Determines recommended action based on conflict analysis
        /// </summary>
        private string DetermineRecommendedAction(List<ImportConflictInfo> conflicts, int manualConflicts)
        {
            if (conflicts.Count == 0)
            {
                return "Execute import - no conflicts detected";
            }

            if (manualConflicts == 0)
            {
                return "Execute import with auto-resolution";
            }

            return $"Review {manualConflicts} manual conflict(s) and provide resolution preferences before importing";
        }

        /// <summary>
        /// Generates human-readable preview message
        /// </summary>
        private string GeneratePreviewMessage(
            int totalConflicts,
            int autoResolvable,
            int manualConflicts,
            ImportChangesPreview preview)
        {
            var parts = new List<string>();

            if (preview.CollectionsToCreate > 0)
            {
                parts.Add($"{preview.CollectionsToCreate} collection(s) to create");
            }

            if (preview.DocumentsToAdd > 0)
            {
                parts.Add($"{preview.DocumentsToAdd} document(s) to add");
            }

            if (preview.DocumentsToUpdate > 0)
            {
                parts.Add($"{preview.DocumentsToUpdate} document(s) to update");
            }

            if (preview.DocumentsToSkip > 0)
            {
                parts.Add($"{preview.DocumentsToSkip} identical document(s) to skip");
            }

            if (totalConflicts > 0)
            {
                parts.Add($"{totalConflicts} conflict(s) detected ({autoResolvable} auto-resolvable, {manualConflicts} require manual resolution)");
            }
            else
            {
                parts.Add("no conflicts detected");
            }

            return string.Join(", ", parts);
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
