using Microsoft.Extensions.Logging;
using Python.Runtime;
using System.Text.Json;

namespace DMMS.Services;

/// <summary>
/// Helper class to handle ChromaDB version compatibility issues
/// Specifically addresses the "_type" configuration issue that affects pre-existing databases
/// </summary>
public static class ChromaCompatibilityHelper
{
    /// <summary>
    /// Attempts to migrate a ChromaDB database to fix "_type" configuration issues
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="dataPath">Path to the ChromaDB data directory</param>
    /// <returns>True if migration was successful or not needed, false if failed</returns>
    public static async Task<bool> MigrateDatabaseAsync(ILogger logger, string dataPath)
    {
        logger.LogInformation($"Checking ChromaDB compatibility for database at: {dataPath}");
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                using var _ = Py.GIL();
                
                // Import required modules
                dynamic sqlite3 = Py.Import("sqlite3");
                dynamic json = Py.Import("json");
                dynamic os = Py.Import("os");
                
                // Find the SQLite database file
                string? sqlitePath = null;
                var candidates = new[] { "chroma.sqlite3", "chroma.db" };
                
                foreach (var candidate in candidates)
                {
                    var candidatePath = Path.Combine(dataPath, candidate);
                    if (File.Exists(candidatePath))
                    {
                        sqlitePath = candidatePath;
                        break;
                    }
                }
                
                if (sqlitePath == null)
                {
                    logger.LogWarning("No ChromaDB SQLite file found - database may be new or corrupted");
                    return true; // Not necessarily an error for new databases
                }
                
                logger.LogInformation($"Found ChromaDB SQLite file: {sqlitePath}");
                
                // Connect to the SQLite database
                dynamic conn = sqlite3.connect(sqlitePath);
                dynamic cursor = conn.cursor();
                
                // Check if collections table exists
                cursor.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='collections';");
                dynamic tableResult = cursor.fetchall();
                
                // Use Python's len() function to get list length
                dynamic builtins = Py.Import("builtins");
                int tableCount = (int)builtins.len(tableResult);
                
                if (tableCount == 0)
                {
                    logger.LogInformation("No collections table found - database appears to be empty");
                    conn.close();
                    return true;
                }
                
                // Check which configuration column exists (different ChromaDB versions use different names)
                cursor.execute("PRAGMA table_info(collections);");
                dynamic columnInfo = cursor.fetchall();
                
                string configColumn = null;
                foreach (dynamic column in columnInfo)
                {
                    string columnName = column[1].ToString();
                    if (columnName == "configuration_json_str")
                    {
                        configColumn = "configuration_json_str";
                        break;
                    }
                    else if (columnName == "config_json_str")
                    {
                        configColumn = "config_json_str";
                        break;
                    }
                }
                
                if (configColumn == null)
                {
                    logger.LogWarning("No configuration column found in collections table - unknown database schema");
                    conn.close();
                    return true; // Not necessarily an error for very new or different schemas
                }
                
                logger.LogInformation($"Using configuration column: {configColumn}");
                
                // Get collections with their configurations
                cursor.execute($"SELECT id, name, {configColumn} FROM collections");
                dynamic rows = cursor.fetchall();
                
                int collectionsCount = (int)builtins.len(rows);
                logger.LogInformation($"Found {collectionsCount} collections to check");
                
                if (collectionsCount == 0)
                {
                    conn.close();
                    return true;
                }
                
                bool needsMigration = false;
                var migrationsNeeded = new List<(string id, string name, string fixedConfig)>();
                
                // Check each collection's configuration
                foreach (dynamic row in rows)
                {
                    string collectionId = row[0].ToString();
                    string collectionName = row[1].ToString();
                    string? configJsonStr = row[2]?.ToString();
                    
                    logger.LogDebug($"Checking collection: {collectionName}");
                    
                    // Handle different configuration patterns based on database schema
                    bool needsConfigFix = false;
                    string? fixedConfig = null;
                    
                    if (string.IsNullOrEmpty(configJsonStr) || configJsonStr.Trim() == "{}")
                    {
                        // For older ChromaDB versions, empty/null config causes '_type' errors in newer versions
                        // We need to add a minimal configuration to make it compatible
                        logger.LogWarning($"Collection {collectionName} has empty configuration (older schema) - will add minimal config for compatibility");
                        fixedConfig = CreateDefaultConfiguration();
                        needsConfigFix = true;
                    }
                    else
                    {
                        try
                        {
                            // Parse the JSON configuration
                            dynamic config = json.loads(configJsonStr);
                            
                            // Check if _type field exists
                            bool hasType = false;
                            try
                            {
                                var typeField = config["_type"];
                                hasType = true;
                            }
                            catch (PythonException ex) when (ex.Message.Contains("KeyError"))
                            {
                                hasType = false;
                            }
                            
                            if (!hasType)
                            {
                                logger.LogWarning($"Collection {collectionName} missing '_type' field - will fix");
                                
                                // Add the missing _type field
                                config["_type"] = new PyString("CollectionConfigurationInternal");
                                fixedConfig = json.dumps(config).ToString();
                                needsConfigFix = true;
                            }
                            else
                            {
                                logger.LogDebug($"Collection {collectionName} configuration is OK");
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError($"Failed to parse configuration for collection {collectionName}: {ex.Message}");
                            fixedConfig = CreateDefaultConfiguration();
                            needsConfigFix = true;
                        }
                    }
                    
                    if (needsConfigFix && fixedConfig != null)
                    {
                        migrationsNeeded.Add((collectionId, collectionName, fixedConfig));
                        needsMigration = true;
                    }
                }
                
                // Apply migrations if needed
                if (needsMigration)
                {
                    logger.LogInformation($"Applying migration to {migrationsNeeded.Count} collections");
                    
                    cursor.execute("BEGIN TRANSACTION");
                    
                    try
                    {
                        foreach (var (id, name, fixedConfig) in migrationsNeeded)
                        {
                            logger.LogInformation($"Updating configuration for collection: {name}");
                            cursor.execute(
                                $"UPDATE collections SET {configColumn} = ? WHERE id = ?",
                                new PyTuple(new PyObject[] { new PyString(fixedConfig), new PyString(id) })
                            );
                        }
                        
                        cursor.execute("COMMIT");
                        logger.LogInformation("Migration completed successfully");
                    }
                    catch (Exception ex)
                    {
                        cursor.execute("ROLLBACK");
                        logger.LogError($"Migration failed, rolled back: {ex.Message}");
                        conn.close();
                        return false;
                    }
                }
                else
                {
                    logger.LogInformation("Database configuration is compatible - no migration needed");
                }
                
                conn.close();
                return true;
                
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during database compatibility check/migration");
                return false;
            }
        }, timeoutMs: 60000, operationName: "MigrateDatabase");
    }
    
    /// <summary>
    /// Creates a default ChromaDB collection configuration compatible with ChromaDB 0.6.x
    /// </summary>
    private static string CreateDefaultConfiguration()
    {
        // For older databases, we need to keep the configuration minimal
        // ChromaDB 0.6.x expects the _type field but not necessarily the complex structure
        var config = new
        {
            _type = "CollectionConfigurationInternal"
        };
        
        return JsonSerializer.Serialize(config);
    }
    
    /// <summary>
    /// Validates that a ChromaDB client can successfully connect and list collections
    /// </summary>
    public static async Task<bool> ValidateClientConnectionAsync(ILogger logger, string dataPath)
    {
        logger.LogInformation("Validating ChromaDB client connection");
        
        return await PythonContext.ExecuteAsync(() =>
        {
            try
            {
                using var _ = Py.GIL();
                
                dynamic chromadb = Py.Import("chromadb");
                dynamic client = chromadb.PersistentClient(path: dataPath);
                
                // Try to list collections - this is where the "_type" error typically occurs
                dynamic collections = client.list_collections();
                
                // Use Python's len() function to get list length
                dynamic builtins = Py.Import("builtins");
                int collectionsCount = (int)builtins.len(collections);
                
                logger.LogInformation($"Successfully connected to ChromaDB - found {collectionsCount} collections");
                return true;
            }
            catch (PythonException ex)
            {
                if (ex.Message.Contains("_type"))
                {
                    logger.LogWarning($"ChromaDB '_type' compatibility issue detected: {ex.Message}");
                }
                else
                {
                    logger.LogError($"ChromaDB connection failed: {ex.Message}");
                }
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError($"Unexpected error connecting to ChromaDB: {ex.Message}");
                return false;
            }
        }, timeoutMs: 30000, operationName: "ValidateClientConnection");
    }
    
    /// <summary>
    /// Performs a comprehensive compatibility check and migration if needed
    /// </summary>
    public static async Task<bool> EnsureCompatibilityAsync(ILogger logger, string dataPath)
    {
        logger.LogInformation("Starting ChromaDB compatibility check");
        
        // First, try to validate connection without migration
        if (await ValidateClientConnectionAsync(logger, dataPath))
        {
            logger.LogInformation("ChromaDB connection successful - no migration needed");
            return true;
        }
        
        // If connection failed, attempt migration
        logger.LogInformation("Connection failed - attempting database migration");
        
        if (!await MigrateDatabaseAsync(logger, dataPath))
        {
            logger.LogError("Database migration failed");
            return false;
        }
        
        // Validate connection again after migration
        if (await ValidateClientConnectionAsync(logger, dataPath))
        {
            logger.LogInformation("Database migration successful - ChromaDB is now compatible");
            return true;
        }
        else
        {
            logger.LogError("Database migration completed but connection still fails");
            return false;
        }
    }
}