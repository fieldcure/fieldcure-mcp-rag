# fieldcure-mcp-rag

C# MCP RAG 서버. 문서 청킹·임베딩·SQLite 벡터 검색.

## 프로젝트 구조

- `src/FieldCure.Mcp.Rag/` — 메인 프로젝트 (net9.0, PackAsTool)
- `tests/FieldCure.Mcp.Rag.Tests/` — 단위 테스트 (MSTest, 42개)
- `scripts/publish-nuget.ps1` — NuGet 배포 (pack → sign → push)
- `design/` — 내부 설계 문서 (gitignore 대상)

## 참조 프로젝트

- `D:\Codes\fieldcure-mcp-filesystem` — 프로젝트 구조(slnx, csproj, Program.cs) 패턴 원본
- `D:\Codes\fieldcure-assiststudio\src\DocumentParsers` — FieldCure.DocumentParsers 소스 (docx, hwpx만 지원)

## 빌드 & 테스트

```bash
dotnet build
dotnet test
```

## v0.1.0 완료 내역

- MCP stdio 서버 (3 tools: index_documents, search_documents, get_document_chunk)
- OpenAI-compatible 임베딩 (Ollama/OpenAI/Azure 등)
- SQLite 벡터 스토어 (WAL, SIMD 코사인 유사도)
- 한글 최적화 청킹 (Regex 문장 경계, 소수점 보호, 괄호 미분할)
- SHA256 증분 인덱싱 + orphan cleanup
- DocumentParserFactory.SupportedExtensions 동적 포맷 감지
- 지원 포맷: .docx, .hwpx, .txt, .md (.pdf/.xlsx/.pptx 미지원)
- NuGet 배포 완료: FieldCure.Mcp.Rag 0.1.0

## v0.2.0 계획: Lucene BM25 + RRF Hybrid Search

설계 문서: `design/IMPLEMENTATION_v0.2.0.md`

### 구현 순서
1. NuGet 추가: Lucene.Net 4.8.*, Analysis.Common, QueryParser
2. `Index/LuceneIndexStore.cs` — Lucene 인덱스 (UpsertChunk, DeleteBySourcePath, Search, Commit)
3. `Search/RrfFusion.cs` — Reciprocal Rank Fusion (k=60)
4. `SqliteVectorStore.GetChunksByIdsAsync` — chunk_id 일괄 조회
5. `Search/HybridSearcher.cs` — BM25 + Vector → RRF, graceful degradation
6. `IndexDocumentsTool` 변경 — Lucene upsert/delete 동시 수행
7. `SearchDocumentsTool` 변경 — HybridSearcher 교체, search_mode 필드 추가
8. 통합 테스트: 한국어 + 영문 전문용어 검증

### 핵심 포인트
- MCP 도구 파라미터 변경 없음 (search_mode 필드만 추가)
- Lucene 경로: `{contextFolder}/.rag/lucene/`
- StandardAnalyzer (한국어 어절 단위, 향후 Nori 교체 가능)
- NullEmbeddingProvider → BM25 only graceful degradation
- AssistStudio 기본 RAG로 사용 예정 (통합 가이드: `design/INTEGRATION_GUIDE.md`)
