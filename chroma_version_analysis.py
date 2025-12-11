#!/usr/bin/env python3
"""
ChromaDB Version Compatibility Analysis
Investigates the specific "_type" configuration issue and provides migration solutions
"""

import chromadb
import json
import sqlite3
import os
import sys
from pathlib import Path

def check_chromadb_version_info():
    """Check ChromaDB version and related information"""
    print("=== ChromaDB Version Information ===")
    print(f"ChromaDB version: {chromadb.__version__}")
    
    # Check if this version has known issues
    version = chromadb.__version__
    if version.startswith("0.6"):
        print("⚠️  WARNING: Version 0.6.x has known compatibility issues with '_type' configuration")
        print("   Recommended: Upgrade to version 1.0.7+ or downgrade to 1.0.0")
    elif version.startswith("1.0") and version < "1.0.7":
        print("⚠️  WARNING: This version may have '_type' configuration issues")
        print("   Recommended: Upgrade to version 1.0.7+")
    elif version >= "1.0.7":
        print("✓ This version should handle '_type' configuration correctly")
    
    return version

def analyze_configuration_error(db_path, collection_name=None):
    """Analyze the specific configuration error in a database"""
    print(f"\n=== Analyzing Configuration Error ===")
    
    # Find the SQLite database
    sqlite_path = None
    for candidate in ['chroma.sqlite3', 'chroma.db']:
        candidate_path = os.path.join(db_path, candidate)
        if os.path.exists(candidate_path):
            sqlite_path = candidate_path
            break
    
    if not sqlite_path:
        print("ERROR: Could not find ChromaDB SQLite file")
        return False
    
    print(f"Analyzing SQLite database: {sqlite_path}")
    
    try:
        conn = sqlite3.connect(sqlite_path)
        cursor = conn.cursor()
        
        # Get collections and their configurations
        if collection_name:
            cursor.execute("SELECT id, name, configuration_json_str FROM collections WHERE name = ?", (collection_name,))
        else:
            cursor.execute("SELECT id, name, configuration_json_str FROM collections")
        
        rows = cursor.fetchall()
        print(f"Found {len(rows)} collections:")
        
        problematic_configs = []
        
        for row in rows:
            collection_id, name, config_json_str = row
            print(f"\nCollection: {name} (ID: {collection_id})")
            
            if config_json_str:
                try:
                    config = json.loads(config_json_str)
                    print(f"  Configuration keys: {list(config.keys())}")
                    
                    # Check for missing '_type' field
                    if '_type' not in config:
                        print(f"  ❌ PROBLEM: Missing '_type' field in configuration")
                        problematic_configs.append((collection_id, name, config))
                        
                        # Try to infer what the _type should be
                        if 'hnsw' in config:
                            print(f"  💡 SUGGESTION: This appears to be an HNSW configuration")
                            config['_type'] = 'CollectionConfigurationInternal'
                    else:
                        print(f"  ✓ Configuration has '_type': {config['_type']}")
                        
                except json.JSONDecodeError as e:
                    print(f"  ❌ PROBLEM: Invalid JSON in configuration: {e}")
                    problematic_configs.append((collection_id, name, None))
            else:
                print(f"  ❌ PROBLEM: No configuration JSON found")
                problematic_configs.append((collection_id, name, None))
        
        conn.close()
        return problematic_configs
        
    except Exception as e:
        print(f"Error analyzing SQLite database: {e}")
        return None

def generate_migration_script(db_path, problematic_configs):
    """Generate SQL script to fix configuration issues"""
    if not problematic_configs:
        print("\n✓ No configuration issues found!")
        return
    
    print(f"\n=== Generating Migration Script ===")
    
    migration_script = f"""-- ChromaDB Configuration Migration Script
-- Generated for database: {db_path}
-- Fixes missing '_type' fields in collection configurations

BEGIN TRANSACTION;

"""
    
    for collection_id, name, config in problematic_configs:
        if config is not None:
            # Add the missing _type field
            if '_type' not in config:
                config['_type'] = 'CollectionConfigurationInternal'
            
            updated_json = json.dumps(config)
            migration_script += f"""-- Fix collection: {name}
UPDATE collections 
SET configuration_json_str = '{updated_json}'
WHERE id = '{collection_id}';

"""
        else:
            # For collections with completely broken configs, use a default
            default_config = {
                "_type": "CollectionConfigurationInternal",
                "hnsw": {},
                "embedding_function": {}
            }
            updated_json = json.dumps(default_config)
            migration_script += f"""-- Fix collection with default config: {name}
UPDATE collections 
SET configuration_json_str = '{updated_json}'
WHERE id = '{collection_id}';

"""
    
    migration_script += """
COMMIT;

-- Verify the changes
SELECT id, name, configuration_json_str FROM collections;
"""
    
    script_path = os.path.join(os.path.dirname(db_path), "chroma_migration.sql")
    with open(script_path, 'w') as f:
        f.write(migration_script)
    
    print(f"Migration script written to: {script_path}")
    print("\nTo apply the migration:")
    print(f"  1. Backup your database: cp -r '{db_path}' '{db_path}_backup'")
    print(f"  2. Apply the script: sqlite3 '{os.path.join(db_path, 'chroma.sqlite3')}' < '{script_path}'")
    print(f"  3. Test the database with ChromaDB")

def test_chromadb_connection_with_fixes(db_path):
    """Test different approaches to connect to the database"""
    print(f"\n=== Testing ChromaDB Connection Approaches ===")
    
    approaches = [
        ("Standard PersistentClient", lambda: chromadb.PersistentClient(path=db_path)),
        ("PersistentClient with settings", lambda: chromadb.PersistentClient(
            path=db_path,
            settings=chromadb.config.Settings(allow_reset=False)
        )),
    ]
    
    for name, client_func in approaches:
        print(f"\nTesting: {name}")
        try:
            client = client_func()
            collections = client.list_collections()
            print(f"  ✓ Success! Found {len(collections)} collections")
            for collection in collections:
                print(f"    - {collection.name}")
            return True
        except Exception as e:
            print(f"  ❌ Failed: {type(e).__name__}: {e}")
    
    return False

def main():
    if len(sys.argv) < 2:
        print("Usage: python chroma_version_analysis.py <path_to_chroma_database> [collection_name]")
        return
    
    db_path = sys.argv[1]
    collection_name = sys.argv[2] if len(sys.argv) > 2 else None
    
    print("ChromaDB Version Compatibility Analysis")
    print("=" * 50)
    
    # Check ChromaDB version
    version = check_chromadb_version_info()
    
    # Analyze the database
    if not os.path.exists(db_path):
        print(f"ERROR: Database path does not exist: {db_path}")
        return
    
    # Test current connection
    success = test_chromadb_connection_with_fixes(db_path)
    
    if not success:
        # Analyze configuration issues
        problematic_configs = analyze_configuration_error(db_path, collection_name)
        
        if problematic_configs:
            generate_migration_script(db_path, problematic_configs)
        
        print(f"\n=== Recommendations ===")
        print("1. IMMEDIATE FIX: Apply the generated migration script")
        print("2. VERSION FIX: Consider upgrading ChromaDB:")
        print("   pip install 'chromadb>=1.0.7'")
        print("3. COMPATIBILITY: Ensure all ChromaDB clients use the same version")
        print("4. BACKUP: Always backup your database before applying migrations")

if __name__ == "__main__":
    main()