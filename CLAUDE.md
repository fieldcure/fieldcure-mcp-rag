# fieldcure-mcp-rag

C# MCP RAG 서버. 문서 청킹·임베딩·SQLite FTS5 + 벡터 하이브리드 검색.

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

## v0.2.0 완료 내역

- FTS5 BM25 전문 검색 (SQLite FTS5 trigram, 한국어 공백 토큰화)
- RRF (Reciprocal Rank Fusion, k=60) — BM25 + 벡터 결과 통합 랭킹
- HybridSearcher — hybrid / bm25_only / vector_only 자동 선택, graceful degradation
- 임베딩 서버 없이도 BM25 키워드 검색 가능 (NullEmbeddingProvider)
- 데이터 경로 이전: `{contextFolder}/.rag/` → `%LOCALAPPDATA%\FieldCure\Mcp.Rag\{hash}\` (자동 마이그레이션)
- search_documents에 search_mode 응답 필드 추가
- 기본 유사도 threshold 0.5 → 0.3 하향
- NuGet 배포: FieldCure.Mcp.Rag 0.2.0
