# Public Datasets for Testing Versioned RAG Systems

Testing a versioned RAG system with Dolt and ChromaDB requires datasets that validate both vector retrieval operations and version control workflows. The optimal test data combines permissive licensing, realistic structure, and built-in version histories. This report identifies **12 primary datasets** across five categories, with specific recommendations for quick iteration testing in the 100-1000 document range.

## Datasets with native version histories offer the strongest testing value

Three datasets stand out for testing CRUD and version control operations because they contain **actual document evolution histories** rather than static snapshots:

**SOTorrent** provides the most granular version tracking available. This academic dataset (CC-BY-SA 4.0) contains **63 million post versions** and **77 million code block versions** from Stack Overflow, with line-level diff operations stored in dedicated tables. The `PostBlockDiff` table includes insert/delete/equal operations [Empirical-software](https://empirical-software.engineering/blog/sotorrent) that directly map to Dolt's diff functionality. Available at [zenodo.org/records/2273117](https://zenodo.org/records/2273117), the MySQL-compatible dump format simplifies loading into Dolt. For testing, sample 500-1000 posts with their full version histories.

**ReleaseEval** (academic license) contains **94,987 release notes** from 3,369 repositories, organized with three task variants: commit messages → release notes, commit tree structures, and code diffs between versions. This naturally models the issue-to-implementation workflow the user described. The hierarchical commit structure tests Dolt's branching operations effectively.

**Libraries.io Open Data** (CC BY-SA 4.0) offers **9.96 million packages** with complete version histories from 33 package managers. Each package entry includes semantic versioning, dependency changes per version, and timestamped releases—ideal for testing merge conflict scenarios where dependencies evolve across branches. Download the ~5GB compressed CSV from [libraries.io/data](https://libraries.io/data).

## GitHub issue datasets for simulating issue implementation logs

|Dataset|License|Size|Format|Version History|Best For|
|---|---|---|---|---|---|
|rIsHu009/github-issues-updated|**MIT**|7,470 issues|Parquet|Timestamps, state changes|CRUD with clear licensing|
|bigcode/the-stack-github-issues|Terms required|30.9M issues|Parquet|Events field with lifecycle|Large-scale stress testing|
|GH Archive (BigQuery)|Public API terms|Billions of events|JSON.gz|IssueEvent actions|Custom repo filtering|
|lewtun/github-issues|Not explicit|3,020 issues|JSON|Timestamps|Quick prototyping|

**Top recommendation**: The **rIsHu009/github-issues-updated** dataset provides the clearest licensing (MIT) with sufficient size (7,470 issues) and rich metadata including comments, reactions, and state transitions. [huggingface](https://huggingface.co/datasets/rIsHu009/github-issues-updated) Load directly via HuggingFace:

python

```python
from datasets import load_dataset
ds = load_dataset('rIsHu009/github-issues-updated')
```

For testing version control operations, the **bigcode/the-stack-github-issues** dataset's `events` field tracks the complete issue lifecycle (opened, commented, closed, reopened) with timestamps—though it requires accepting Hugging Face terms.

## Technical documentation with consistent structure

**TLDR Pages** (CC-BY 4.0) provides **~3,000 command documentation pages** in markdown format with **20,000+ Git commits** showing real documentation evolution. The uniform structure—title, description, 5-8 examples per page—creates predictable chunking for vector retrieval testing. The hierarchical `pages/{platform}/{command}.md` organization tests category-based queries. Clone from [github.com/tldr-pages/tldr](https://github.com/tldr-pages/tldr).

**Hugging Face Transformers docs** (Apache 2.0) offer ~500-800 pages mixing tutorials, API references, and conceptual guides. Git tags for each release (v4.0, v4.35, etc.) provide natural version boundaries for testing branch/merge operations. Extract `/docs/source/en/` after cloning.

**ArchWiki** (GFDL) provides 5,741 files [Arch Linux](https://archlinux.org/packages/extra/any/arch-wiki-docs/files/) of technical Linux documentation with category hierarchies and cross-references—useful for testing link resolution in RAG retrieval, though the GFDL license requires share-alike.

## Stack Overflow data with edit histories

The **official Stack Exchange data dump** (CC-BY-SA 4.0) includes a critical `PostHistory.7z` file containing complete edit histories with revision GUIDs and change types. The last public dump (April 2024) is available at [archive.org/details/stackexchange](https://archive.org/details/stackexchange). For smaller testing, use Brent Ozar's SQL Server versions (1.5GB-50GB) via BitTorrent.

**StaQC** (CC-BY 4.0) provides **148K Python and 120K SQL question-code pairs** pre-filtered from Stack Overflow—ideal for domain-specific RAG testing without processing the full dump. Available at [github.com/LittleYUYU/StackOverflow-Question-Code-Dataset](https://github.com/LittleYUYU/StackOverflow-Question-Code-Dataset).

For immediate testing without data processing:

python

```python
from datasets import load_dataset
ds = load_dataset('c17hawke/stackoverflow-dataset')  # 25K curated Q&A pairs
```

## RAG benchmark datasets adaptable for version testing

**BEIR benchmark** (Apache 2.0 framework) includes 15 public datasets with standardized JSONL format. Three are optimally sized for integration testing:

- **SciFact**: 5,183 documents, 300 queries—scientific fact verification
- **NFCorpus**: 3,633 documents, 323 queries—biomedical retrieval
- **ArguAna**: 8,674 documents, 1,406 queries—argument retrieval

Download directly:

python

```python
from beir import util
from beir.datasets.data_loader import GenericDataLoader

url = "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/scifact.zip"
data_path = util.download_and_unzip(url, "datasets")
corpus, queries, qrels = GenericDataLoader(data_folder=data_path).load(split="test")
```

**TriviaQA** (Apache 2.0) provides 650K+ question-answer-evidence triples requiring cross-sentence reasoning—useful for testing multi-document retrieval. **Natural Questions** (CC BY-SA 3.0) offers 307K real Google search queries with Wikipedia answers.

## Synthetic test data generation and vector DB fixtures

### ChromaDB test patterns

ChromaDB's test suite uses simple fixtures with tolerance-based vector comparison:

python

```python
# From chromadb/test/test_api.py
documents = ["This is document1", "This is document2"]
metadatas = [{"source": "notion"}, {"source": "google-docs"}]
ids = ["id1", "id2"]

collection.add(documents=documents, metadatas=metadatas, ids=ids)
```

[GitHub](https://github.com/chroma-core/chroma)

The **chromadb-ecosystem-tests** repository ([github.com/amikos-tech/chromadb-ecosystem-tests](https://github.com/amikos-tech/chromadb-ecosystem-tests)) provides additional integration patterns. [GitHub](https://github.com/amikos-tech/chromadb-ecosystem-tests)

### Generating reproducible test embeddings

Combine Faker for text generation with sentence-transformers for embeddings:

python

```python
from faker import Faker
from sentence_transformers import SentenceTransformer

fake = Faker()
Faker.seed(42)  # Reproducible across test runs

model = SentenceTransformer('all-MiniLM-L6-v2')

test_docs = [fake.paragraph() for _ in range(500)]
test_embeddings = model.encode(test_docs)
test_metadata = [{
    "author": fake.name(),
    "date": str(fake.date()),
    "version": f"1.{i % 5}"  # Simulate versions
} for i in range(500)]
```

### Edge cases to test

Vector database edge cases that commonly reveal bugs:

- **Near-duplicate vectors**: Test recall precision with cosine similarity > 0.99
- **Filter selectivity extremes**: Test with 50% vs 99.9% filtered results
- **Concurrent operations**: Update documents while searches execute
- **Null metadata handling**: Fields missing in some documents
- **Batch mixed results**: Operations where some items succeed and others fail

### VDBBench standard format

For benchmarking across vector databases, use the VDBBench parquet format:

```
train.parquet: ID column + vector column (numpy arrays)
test.parquet: Same structure  
neighbors.parquet: ID + neighbors array (ground truth)
```

## Dolt-specific version testing approaches

Dolt's test suite uses **sqllogictest** with 6.7 million test statements. [DoltHub](https://www.dolthub.com/blog/2019-10-22-testing-dolts-sql-engine/) For RAG integration testing, leverage Dolt's versioning primitives:

sql

```sql
-- Create version checkpoint
CALL dolt_commit('-Am', 'Adding initial documents');

-- Create branch for testing updates
CALL dolt_branch('document-updates');
CALL dolt_checkout('document-updates');

-- Make changes and compare
UPDATE documents SET content = 'Modified content' WHERE id = 1;
SELECT * FROM dolt_diff_summary('main', 'document-updates');
```

Dolt now supports **native vector indexes** (v1.47.0+) that maintain version history:

sql

```sql
CREATE TABLE documents (
    id INT PRIMARY KEY,
    content TEXT,
    embedding VECTOR(768)
);
ALTER TABLE documents ADD VECTOR INDEX embedding_idx (embedding);
```

## Recommended test corpus by use case

|Use Case|Recommended Dataset|Documents|Why|
|---|---|---|---|
|Unit tests|SciFact subset|100-200|Clean structure, fast iteration|
|CRUD integration|TLDR pages (`pages/common/`)|~1,200|Real version history, consistent format|
|Version control testing|SOTorrent sample|500-1000 posts|Block-level diffs, edit timestamps|
|Issue log simulation|rIsHu009/github-issues-updated|7,470|MIT license, state transitions|
|Full RAG evaluation|SciFact + NFCorpus|~9,000|Standard benchmark with queries|

## Preprocessing requirements

|Dataset|Preprocessing Needed|
|---|---|
|TLDR Pages|None—markdown files ready to use|
|SciFact/BEIR|Minimal—JSONL with title/text fields|
|SOTorrent|SQL import, then filter relevant tables|
|GitHub Issues (HF)|None—load via datasets library|
|Stack Exchange dump|7z extraction, XML parsing|
|Libraries.io|CSV parsing, filter to relevant packages|

## Conclusion

For a versioned RAG system testing Dolt and ChromaDB integration, prioritize **SOTorrent** for its granular edit histories, **TLDR Pages** for consistent document structure with real Git history, and **SciFact** for standardized retrieval evaluation. All three are permissively licensed (CC-BY-SA, CC-BY, Apache 2.0 respectively) and sized appropriately for quick iteration. Generate synthetic version progressions using Faker with seeded randomness for reproducible test runs, and leverage VDBBench's parquet format if comparing across multiple vector databases.