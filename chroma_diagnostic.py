#!/usr/bin/env python3
"""
ChromaDB Database Diagnostics Script
Investigates the structure of ChromaDB databases to understand compatibility issues
"""

import chromadb
import json
import sqlite3
import os
import sys
from pathlib import Path

def analyze_chroma_database(db_path):
    """Analyze the ChromaDB database structure and metadata"""
    print(f"=== Analyzing ChromaDB at: {db_path} ===")
    print(f"ChromaDB version: {chromadb.__version__}")
    
    if not os.path.exists(db_path):
        print(f"ERROR: Database path does not exist: {db_path}")
        return False
    
    # List files in the database directory
    print(f"\nDatabase files:")
    for item in os.listdir(db_path):
        item_path = os.path.join(db_path, item)
        if os.path.isfile(item_path):
            size = os.path.getsize(item_path)
            print(f"  {item} ({size} bytes)")
        else:
            print(f"  {item}/ (directory)")
    
    # Look for SQLite database files
    sqlite_files = [f for f in os.listdir(db_path) if f.endswith('.sqlite') or f.endswith('.db') or 'chroma' in f.lower()]
    print(f"\nPotential SQLite files: {sqlite_files}")
    
    # Try to analyze the main ChromaDB SQLite database
    chroma_db_path = None
    for candidate in ['chroma.sqlite3', 'chroma.db', 'database.db']:
        candidate_path = os.path.join(db_path, candidate)
        if os.path.exists(candidate_path):
            chroma_db_path = candidate_path
            break
    
    if not chroma_db_path:
        # Look for any .sqlite3 file
        for file in os.listdir(db_path):
            if file.endswith('.sqlite3'):
                chroma_db_path = os.path.join(db_path, file)
                break
    
    if chroma_db_path:
        print(f"\nAnalyzing SQLite database: {chroma_db_path}")
        analyze_sqlite_structure(chroma_db_path)
    else:
        print("WARNING: No SQLite database file found")
    
    # Try to connect with ChromaDB client
    print(f"\n=== Testing ChromaDB Client Connection ===")
    try:
        client = chromadb.PersistentClient(path=db_path)
        print("✓ Successfully connected to database")
        
        # Try to list collections
        try:
            collections = client.list_collections()
            print(f"✓ Successfully listed collections: {len(collections)} found")
            for collection in collections:
                print(f"  - {collection.name} (id: {collection.id})")
        except Exception as e:
            print(f"✗ Error listing collections: {e}")
            print(f"  Error type: {type(e).__name__}")
            return False
            
    except Exception as e:
        print(f"✗ Error connecting to database: {e}")
        print(f"  Error type: {type(e).__name__}")
        return False
    
    return True

def analyze_sqlite_structure(sqlite_path):
    """Analyze the SQLite database structure"""
    try:
        conn = sqlite3.connect(sqlite_path)
        cursor = conn.cursor()
        
        # Get table list
        cursor.execute("SELECT name FROM sqlite_master WHERE type='table';")
        tables = cursor.fetchall()
        print(f"  Tables: {[table[0] for table in tables]}")
        
        # Check collections table specifically
        if ('collections',) in tables:
            print(f"\n  Collections table structure:")
            cursor.execute("PRAGMA table_info(collections);")
            columns = cursor.fetchall()
            for column in columns:
                print(f"    {column[1]} ({column[2]})")
            
            # Check collection data
            cursor.execute("SELECT id, name, configuration_json_str FROM collections LIMIT 5;")
            rows = cursor.fetchall()
            print(f"  Sample collection data ({len(rows)} rows):")
            for row in rows:
                config_json = row[2] if len(row) > 2 else None
                print(f"    ID: {row[0]}, Name: {row[1]}")
                if config_json:
                    try:
                        config = json.loads(config_json)
                        print(f"    Configuration: {json.dumps(config, indent=6)}")
                    except json.JSONDecodeError as e:
                        print(f"    Configuration (invalid JSON): {config_json}")
                        print(f"    JSON Error: {e}")
        
        conn.close()
    except Exception as e:
        print(f"  Error analyzing SQLite: {e}")

def test_database_migration(db_path):
    """Test if we can migrate the database to current version"""
    print(f"\n=== Testing Database Migration ===")
    
    # Create a backup first
    import shutil
    backup_path = db_path + "_backup"
    if not os.path.exists(backup_path):
        print(f"Creating backup at: {backup_path}")
        shutil.copytree(db_path, backup_path)
    
    try:
        # Try different ChromaDB client configurations
        configs = [
            {"path": db_path},
            {"path": db_path, "allow_reset": False},
        ]
        
        for i, config in enumerate(configs):
            print(f"  Trying configuration {i+1}: {config}")
            try:
                client = chromadb.PersistentClient(**config)
                collections = client.list_collections()
                print(f"    ✓ Success with {len(collections)} collections")
                return True
            except Exception as e:
                print(f"    ✗ Failed: {e}")
    
    except Exception as e:
        print(f"Migration test failed: {e}")
    
    return False

def main():
    if len(sys.argv) < 2:
        print("Usage: python chroma_diagnostic.py <path_to_chroma_database>")
        print("Example: python chroma_diagnostic.py C:/path/to/chroma_data")
        return
    
    db_path = sys.argv[1]
    
    print("ChromaDB Database Diagnostic Tool")
    print("=" * 50)
    
    success = analyze_chroma_database(db_path)
    
    if not success:
        print(f"\n=== Attempting Migration/Repair ===")
        test_database_migration(db_path)
    
    print(f"\n=== Recommendations ===")
    if success:
        print("✓ Database appears to be compatible with current ChromaDB version")
    else:
        print("✗ Database has compatibility issues. Possible solutions:")
        print("  1. Upgrade/downgrade ChromaDB to match the database version")
        print("  2. Migrate the database using ChromaDB migration tools")
        print("  3. Export data from old database and import to new one")
        print("  4. Check if there are any ChromaDB configuration flags to handle legacy databases")

if __name__ == "__main__":
    main()