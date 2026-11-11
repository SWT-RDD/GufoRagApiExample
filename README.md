# GufoRAG Chat API 範例（C#）

示範外部應用如何呼叫 GufoRAG chatbot API：建立 config → SSE 問答 → 查聊天室／聊天記錄 → 評價 → 刪除 config。

- 執行：`dotnet run`（`Program.cs` 的 `baseUrl` 預設 `http://localhost:5555`，即 reverse proxy 的 `HTTP_SERVICE_PORT`）
- 本文件只列整合重點；**完整欄位、界線與行為說明以 `GufoRAG_chatbot/chatbot/README.md` 為準**

## 整合建議

**chatbot API 刻意不做 authentication，請由外部應用的「後端」呼叫，不要直接暴露給前端。**

1. **資安**：前端直連等於把 API 公開給任何人。由後端代理呼叫，沿用外部應用既有的 authentication 即可。
2. **串流完整性**：AI 回覆在**串流結束後才寫入**。前端中途關頁會中斷串流、該次回覆不會被儲存；由後端呼叫則可持續接收到結束。

## API 一覽

| 方法 | 路徑 | 說明 |
| ---- | ---- | ---- |
| POST | `/api/chat/chatbot` | 問答（SSE 串流） |
| GET | `/api/chat/chatrooms` | 聊天室清單（分頁，預設 100 筆） |
| GET | `/api/chat/chatrooms/user/{user_id}` | 某使用者的聊天室（含 `total_count`） |
| GET | `/api/chat/chatrooms/{chat_room_id}/chatlogs` | 某聊天室的聊天記錄（分頁） |
| PATCH | `/api/chat/chatrooms/{chat_room_id}` | 修改聊天室標題（手動改名） |
| DELETE | `/api/chat/chatrooms/{chat_room_id}` | 刪除聊天室（軟刪除） |
| GET | `/api/chat/chatlogs` | 聊天記錄查詢（可依 user_id／時間等過濾，分頁） |
| GET | `/api/chat/chatlogs/{chat_log_id}` | 單筆聊天記錄 |
| POST | `/api/chat/chatlogs/{chat_log_id}/rating` | 評價 |
| GET／PUT | `/api/config/` | 讀取／局部更新預設配置 |
| GET | `/api/config/list` | 配置清單（分頁） |
| GET／POST／PUT／DELETE | `/api/config/{config_name}` | 讀取／建立／更新／刪除指定配置 |

> 列表端點**一律分頁**：不帶 `limit` 只回第一頁 100 筆（上限 1000），要拿全部請自己用 `offset` 翻頁。

## 問答 API（SSE 串流）

`POST http://localhost:5555/api/chat/chatbot`，`Content-Type: application/json`

### 請求資料範例
```json
{
  "chat_room_id": null,
  "chat_log_id": null,
  "human_content": "請問什麼是人工智慧？",
  "config_name": "default",
  "user_id": "user123",
  "selected_index": ["technical_docs", "faq_docs"],
  "dsl": "$privileges in [10, 20] and $containsAny in [1, 2, 3]",
  "document_ids": ["doc_001_ai_intro"]
}
```

| 欄位 | 說明 |
| ---- | ---- |
| chat_room_id | `null`＝新建聊天室；續談帶上次 `chat_room` 塊回傳的 `id` |
| chat_log_id | 一般為 `null`；帶值＝從該則之後分支續談 |
| human_content | 使用者輸入，1–2000 字 |
| config_name | 使用的配置，預設 `default` |
| user_id / tenant_id / tag | 選填，歸屬與過濾用；**建室當下凍結** |
| selected_index | 選填，本次檢索的索引；未給用 config 值，皆空＝搜尋全部索引 |
| dsl | 選填，篩選條件 DSL 字串（取代已移除的 `contains_any`／`privileges`／`filter_text`）。括號／引號不平衡回 400 |
| document_ids | 選填，只在這些文件內檢索 |

- **未知欄位一律回 422**（`extra="forbid"`），打錯欄位名不會被靜默忽略。
- 其餘進階欄位（`index_tiers`、`single_prompt`、`policy_engine`…）見 chatbot README。

### curl 請求範例
```
curl -X POST http://localhost:5555/api/chat/chatbot \
  -H "Content-Type: application/json" \
  -d '{
    "chat_room_id": null,
    "chat_log_id": null,
    "human_content": "請問什麼是人工智慧？",
    "config_name": "default",
    "user_id": "user123",
    "selected_index": ["technical_docs", "faq_docs"],
    "dsl": "$privileges in [10, 20] and $containsAny in [1, 2, 3]",
    "document_ids": ["doc_001_ai_intro"]
  }'
```

### ⚠️ 這支端點不保證回 SSE

串流開始前就能判定的錯誤（聊天室／記錄不存在、輸入不合法、超過每日額度…）會回**一般 JSON 錯誤信封**，`Content-Type` 是 `application/json`。請先檢查 HTTP status，成功才掛 SSE 解析器（`Program.cs` 的 `ChatWithBot` 即此做法）。

### 串流塊類型

每行以 `data: ` 開頭，內容為 JSON：`{"chunk_type": ..., "content": ..., "data": {...}}`（另有恆為 `null` 的 `session_id`，忽略即可）。

| chunk_type | 說明 | 處理方式 |
| ---------- | ---- | -------- |
| message | 回答內容（逐段） | 將 `content` 逐段累加即為完整回答 |
| thinking | 模型推理過程（支援的模型才有） | 與回答分開顯示，或忽略 |
| status | 處理階段事件 | 顯示用，可忽略 |
| agent_tool_call / agent_tool_result | Agent 模式的工具呼叫／結果 | 顯示用，可忽略 |
| chat_room | 聊天室資訊 | 對話完成後送一次；記下 `id` 供續談、`latest_chat_log_id` 供評價 |
| error | 處理中發生錯誤 | **終止**；依 `data.code` 分流（見錯誤代碼表），回報問題時附 `data.error_id` |
| end | 正常結束 | **終止**；被合規閘擋下時 `data` 另帶 `blocked: true` |

`error` 與 `end` 只會出現其中一個，收到任一個即可結束讀取。

#### 推理塊 (thinking)
```
data: {"chunk_type": "thinking", "content": "我需要先釐清使用者問的是...", "data": {"content": "我需要先釐清使用者問的是...", "timestamp": "2026-06-03T10:30:00Z"}}
```

#### 訊息塊 (message)
```
data: {"chunk_type": "message", "content": "人工智慧是", "data": {"content": "人工智慧是", "timestamp": "2024-01-01T10:00:03+00:00"}}
```

#### 聊天室資訊塊 (chat_room)
（為求可讀排版成多行，實際每個 frame 都是單行。）
```
data: {
  "chunk_type": "chat_room",
  "content": "",
  "data": {
    "id": 123,
    "title": "AI助手對話",
    "title_source": "auto",
    "description": "一般AI對話",
    "role": "智能助手",
    "model_name": "openai:gpt-5-mini",
    "status": "active",
    "selected_index": ["technical_docs", "faq_docs"],
    "dsl": "$privileges in [10, 20] and $containsAny in [1, 2, 3]",
    "document_ids": ["doc_001_ai_intro"],
    "search_selected_number": 8,
    "search_total_number": 16,
    "data_source_ratio": 0.5,
    "use_knowledge_mode": "strict",
    "enable_rerank": false,
    "chat_logs_count": 1,
    "latest_chat_log_id": 456,
    "grounding_no_answer": false,
    "suggest_questions": ["相關問題1", "相關問題2"],
    "search_results": [
      {
        "doc_name": "AI技術介紹.pdf",
        "document_id": "doc_001_ai_intro",
        "chunk_index": 2,
        "data_source": "pdf_documents",
        "index": "technical_docs",
        "search_mode": "hybrid",
        "last_modified": "2024-01-01T10:30:00Z",
        "score": 0.8745,
        "source_no": 1,
        "document": {
          "title": "人工智慧基礎概念",
          "content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學...",
          "date": "2024-01-01",
          "category": "技術文件",
          "search": "人工智慧基礎概念 人工智慧（Artificial Intelligence, AI）是一門跨領域的科學..."
        }
      }
    ],
    "usage": {"input_tokens": 1200, "output_tokens": 350, "total_tokens": 1550, "model": "openai:gpt-5-mini"},
    "latency_ms": 5200,
    "ttft_ms": 2100,
    "created_at": "2024-01-01T10:00:00Z",
    "updated_at": "2024-01-01T12:00:00Z"
  }
}
```

`data` 除上列欄位外，還包含該聊天室建室當下的 config 快照（見「配置參數」）。`grounding_no_answer: true` 表示本輪判定素材不足、走了「查無答案」的回覆。

#### 錯誤塊 (error)
```
data: {"chunk_type": "error", "content": "輸入超過語言模型的上下文長度上限，請縮短內容或開啟新對話: This model's maximum context length is 8192 tokens...", "data": {"status": "error", "timestamp": "2024-01-01T10:00:05+00:00", "code": 2024, "error_id": "3f9c0a1b2d4e", "detail": "This model's maximum context length is 8192 tokens..."}}
```
- `data.code`：與錯誤代碼表同一套碼，**請依它分流，不要比對 `content` 文字**
- `data.error_id`：chatbot 日誌的對照編號
- `data.detail`：成因（已遮罩，但可能含技術細節），不建議直接顯示給終端使用者

#### 結束塊 (end)
```
data: {"chunk_type": "end", "content": "", "data": {"status": "completed", "timestamp": "2024-01-01T10:00:05+00:00"}}
```

### search_results（檢索來源）

| 欄位 | 說明 |
| ---- | ---- |
| doc_name / document_id | 文件名稱／ID |
| chunk_index | 文件分塊索引（從 0 開始） |
| index / data_source | 所屬索引／資料來源 |
| search_mode | 檢索策略 |
| last_modified | 最後修改時間（可能為 `null`） |
| score | 相關性分數 |
| source_no | 來源編號，回答中的 `[[N]]` 即指 `source_no == N` 的來源；`null`＝未提供給模型 |
| document | 文件內容，欄位取決於匯入時的資料（`search` 為搜尋欄位） |

```json
"search_results": [
  {
    "doc_name": "AI技術介紹.pdf",
    "document_id": "doc_001_ai_intro",
    "chunk_index": 2,
    "data_source": "pdf_documents",
    "index": "technical_docs",
    "search_mode": "hybrid",
    "last_modified": "2024-01-01T10:30:00Z",
    "score": 0.8745,
    "source_no": 1,
    "document": {
      "title": "人工智慧基礎概念",
      "content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學，其目標是創造能夠執行通常需要人類智慧的任務的機器...",
      "date": "2024-01-01",
      "category": "技術文件",
      "author": "技術團隊",
      "summary": "本文介紹了人工智慧的基本概念和應用領域",
      "search": "人工智慧基礎概念 人工智慧（Artificial Intelligence, AI）是一門跨領域的科學，其目標是創造能夠執行通常需要人類智慧的任務的機器..."
    }
  },
  {
    "doc_name": "機器學習指南.docx",
    "document_id": "doc_002_ml_guide",
    "chunk_index": 0,
    "data_source": "word_documents",
    "index": "technical_docs",
    "search_mode": "hybrid",
    "last_modified": "2024-01-02T14:20:00Z",
    "score": 0.7832,
    "source_no": 2,
    "document": {
      "title": "機器學習入門指南",
      "content": "機器學習是人工智慧的一個重要分支，它使電腦能夠在沒有明確程式設計的情況下學習和改進...",
      "date": "2024-01-02",
      "category": "教學文件",
      "author": "研發部",
      "summary": "詳細說明機器學習的基本原理和方法",
      "search": "機器學習入門指南 機器學習是人工智慧的一個重要分支，它使電腦能夠在沒有明確程式設計的情況下學習和改進..."
    }
  }
]
```

## 聊天室 API

### 聊天室清單
```
curl http://localhost:5555/api/chat/chatrooms
curl "http://localhost:5555/api/chat/chatrooms?limit=100&offset=100"
```
`json_data` 直接是聊天室陣列（無 `total_count`），每列欄位同下方範例。

### 根據使用者ID獲取聊天室
```
curl http://localhost:5555/api/chat/chatrooms/user/user123
curl "http://localhost:5555/api/chat/chatrooms/user/user123?limit=10&offset=10"
```

回應（`json_data` 為信封，含分頁資訊；範例為節錄，實際每列含完整 config 快照）：
```json
{
  "json_data": {
    "chat_rooms": [
      {
        "id": 1,
        "title": "AI助手對話",
        "title_source": "auto",
        "description": "一般AI對話",
        "role": "智能助手",
        "chatroom_system_prompt": "你是一個專業的AI助手...",
        "product_system_prompt": "產品相關提示詞...",
        "intension_system_prompt": "意圖分析提示詞...",
        "status": "active",
        "model_name": "openai:gpt-4.1-mini",
        "active_chain_end_id": null,
        "selected_index": ["technical_docs", "faq_docs"],
        "dsl": "$privileges in [10, 20] and $containsAny in [1, 2, 3]",
        "search_selected_number": 5,
        "search_total_number": 10,
        "data_source_ratio": 0.7,
        "use_knowledge_mode": "strict",
        "enable_rerank": false,
        "memory_count": 3,
        "response_format": "markdown",
        "enable_suggest_questions": true,
        "enable_title": true,
        "update_title_only_once": false,
        "temperature": 0.7,
        "document_field_mapping": {},
        "chat_logs_count": 5,
        "created_at": "2024-01-01T10:00:00Z",
        "updated_at": "2024-01-01T12:00:00Z"
      }
    ],
    "total_count": 5,
    "returned_count": 1,
    "user_id": "user123"
  },
  "error": false,
  "message": "操作成功",
  "code": 0,
  "http_status": 200
}
```

### 聊天室標題怎麼來

- 自動命名由 config 的 `enable_title`（預設開）控制：標題與描述由同一次 LLM 呼叫產生；`update_title_only_once`
  開著時只在還沒命名時寫一次，關著（預設）則每輪依最新對話重寫。
- `title_source` 表示目前標題的來源：`null` 尚未命名、`auto` 系統命名、`manual` 使用者改過。
- 推薦問題由 `enable_suggest_questions` 另外控制，兩者互不影響；兩者都不需要產出時，該輪不會多呼叫 LLM。

### 修改聊天室標題
```
curl -X PATCH http://localhost:5555/api/chat/chatrooms/1 \
  -H "Content-Type: application/json" \
  -d '{"title": "年度預算討論"}'
```
回應的 `json_data` 是房間資料，欄位與聊天室清單的單一項目相同（以下為節錄）：
```json
{
  "json_data": {
    "id": 1,
    "title": "年度預算討論",
    "title_source": "manual",
    "chat_logs_count": 3
  },
  "error": false,
  "message": "操作成功",
  "code": 0,
  "http_status": 200
}
```
- 請求體只有 `title` 一欄，多送其他欄位回 422。
- 標題去頭尾空白後不可為空、不可含控制字元（含換行與 tab）、不可超過長度上限，不合法回 400 `INVALID_INPUT`（1001），`message` 後綴帶實際上限。
- 房間不存在或已刪除回 404 `CHAT_ROOM_NOT_FOUND`。
- 改名後 `title_source` 變成 `manual`：系統**不再自動改這個標題**（包括改名當下正在進行的那一輪），描述仍會照設定自動更新。目前沒有「還原成自動命名」的方式。
- 手動標題是使用者自己輸入的文字，不會被輸出遮蔽規則清空或替換。
- 不檢查 `user_id`：由呼叫端（外部應用後端）確認使用者有權改這間房。

### 刪除聊天室
```
curl -X DELETE http://localhost:5555/api/chat/chatrooms/1
```
```json
{
  "json_data": {
    "chat_room_id": 1,
    "status": "deleted",
    "cascaded_chat_logs": 12,
    "cascade_complete": true
  },
  "error": false,
  "message": "操作成功",
  "code": 0,
  "http_status": 200
}
```
軟刪除；重複刪除同一間房會回 404 `CHAT_ROOM_NOT_FOUND`。

## 聊天記錄 API

### 單一聊天室的聊天記錄
```
curl http://localhost:5555/api/chat/chatrooms/1/chatlogs
curl "http://localhost:5555/api/chat/chatrooms/1/chatlogs?limit=100&offset=100"
```
```json
{
  "json_data": [
    {
      "id": 1,
      "chat_room_id": 1,
      "previous_chat_log_id": null,
      "human_content": "請問什麼是人工智慧？",
      "ai_content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學，其目標是創造能夠執行通常需要人類智慧的任務的機器...",
      "human_time": "2024-01-01T10:00:00+08:00",
      "ai_time": "2024-01-01T10:00:05+08:00",
      "suggest_questions": ["機器學習和AI有什麼差別？", "AI有哪些實際應用？"],
      "search_results": [
        {
          "doc_name": "AI技術介紹.pdf",
          "document_id": "doc_001_ai_intro",
          "chunk_index": 2,
          "data_source": "pdf_documents",
          "index": "technical_docs",
          "search_mode": "hybrid",
          "last_modified": "2024-01-01T10:30:00Z",
          "score": 0.8745,
          "source_no": 1,
          "document": {
            "title": "人工智慧基礎概念",
            "content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學...",
            "date": "2024-01-01",
            "category": "技術文件",
            "search": "人工智慧基礎概念 人工智慧（Artificial Intelligence, AI）是一門跨領域的科學..."
          }
        }
      ],
      "language": "繁體中文",
      "query_start_time": "2024-01-01T10:00:01+08:00",
      "query_end_time": "2024-01-01T10:00:03+08:00",
      "keywords": ["人工智慧", "AI", "技術"],
      "question": "使用者詢問人工智慧的定義和概念"
    }
  ],
  "error": false,
  "message": "操作成功",
  "code": 0,
  "http_status": 200
}
```

### 根據ID獲取特定聊天記錄
```
curl http://localhost:5555/api/chat/chatlogs/1
```
```json
{
  "json_data": {
    "id": 1,
    "chat_room_id": 1,
    "chat_room_title": "AI助手對話",
    "user_id": "user123",
    "previous_chat_log_id": null,
    "human_content": "請問什麼是人工智慧？",
    "ai_content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學...",
    "human_time": "2024-01-01T10:00:00+08:00",
    "ai_time": "2024-01-01T10:00:05+08:00",
    "suggest_questions": ["機器學習和AI有什麼差別？", "AI有哪些實際應用？"],
    "search_results": [...],
    "language": "繁體中文",
    "query_start_time": "2024-01-01T10:00:01+08:00",
    "query_end_time": "2024-01-01T10:00:03+08:00",
    "keywords": ["人工智慧", "AI", "技術"],
    "question": "使用者詢問人工智慧的定義和概念",
    "rating_type": null,
    "rating_feedback": null,
    "rating_time": null
  },
  "error": false,
  "message": "操作成功",
  "code": 0,
  "http_status": 200
}
```

### 列出聊天記錄（過濾、分頁）

常用查詢參數：`user_id`、`tenant_id`、`tag`、`keyword`（≥2 字）、`rating_type`（`positive`／`negative`／`unrated`）、`start_time`／`end_time`（ISO 8601）、`limit`（預設 100，上限 1000）、`offset`。完整清單見 chatbot README。

```
curl "http://localhost:5555/api/chat/chatlogs?user_id=user123"
curl "http://localhost:5555/api/chat/chatlogs?start_time=2024-01-01T00:00:00Z&end_time=2024-01-31T23:59:59Z"
curl "http://localhost:5555/api/chat/chatlogs?user_id=user123&limit=50&offset=100"
```
```json
{
  "json_data": {
    "chat_logs": [
      {
        "id": 1,
        "chat_room_id": 1,
        "previous_chat_log_id": null,
        "human_content": "請問什麼是人工智慧？",
        "ai_content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學...",
        "human_time": "2024-01-01T10:00:00Z",
        "ai_time": "2024-01-01T10:00:05Z",
        "suggest_questions": ["機器學習和AI有什麼差別？", "AI有哪些實際應用？"],
        "search_results": [...],
        "language": "繁體中文",
        "query_start_time": "2024-01-01T10:00:01Z",
        "query_end_time": "2024-01-01T10:00:03Z",
        "keywords": ["人工智慧", "AI", "技術"],
        "question": "使用者詢問人工智慧的定義和概念",
        "rating_type": null,
        "rating_feedback": null,
        "rating_time": null
      }
    ],
    "total_count": 150,
    "returned_count": 1,
    "limit": 100,
    "offset": 0,
    "filters_applied": {
      "user_id": null,
      "tenant_id": null,
      "tag": null,
      "exclude_tags": null,
      "keyword": null,
      "rating_type": null,
      "answer_source": null,
      "prompt_version": null,
      "config_name": null,
      "config_version": null,
      "config_version_gte": null,
      "config_version_lt": null,
      "start_time": null,
      "end_time": null
    }
  },
  "error": false,
  "message": "操作成功",
  "code": 0,
  "http_status": 200
}
```

### 提交評價
`rating_type`：`positive`（好評）／`negative`（壞評），必填；`feedback`：選填，上限 2000 字。
```
curl -X POST http://localhost:5555/api/chat/chatlogs/1/rating \
  -H "Content-Type: application/json" \
  -d '{
    "rating_type": "positive",
    "feedback": "回答很有幫助"
  }'
```
```json
{
  "json_data": {
    "chat_log_id": 1,
    "tag": null,
    "prompt_version": 3,
    "single_prompt": null,
    "rating_type": "positive",
    "rating_feedback": "回答很有幫助",
    "rating_time": "2024-01-01T10:05:00Z"
  },
  "error": false,
  "message": "評價提交成功",
  "code": 0,
  "http_status": 200
}
```

## 配置 API

### 配置參數（常用）
寫入為**局部更新**（只送要改的欄位），未知欄位回 422。完整清單見 chatbot README。

| 參數名稱 | 類型 | 說明 |
| -------- | ---- | ---- |
| product_system_prompt | string | 產品系統提示詞 |
| role | string | 機器人身份設定 |
| model_name | string | 使用的 AI 模型名稱（如 `openai:gpt-5-mini`） |
| search_selected_number | integer | 實際放進上下文的搜尋結果數量 |
| search_total_number | integer | 檢索的總搜尋結果數量 |
| data_source_ratio | float | 詞／向量搜尋比例（0.0＝純向量，1.0＝純關鍵字） |
| use_knowledge_mode | string | 知識使用模式：`none`／`assist`／`strict` |
| enable_rerank | boolean | 是否重新排序搜尋結果 |
| memory_count | integer | 記住的歷史對話數量 |
| enable_suggest_questions | boolean | 是否產生推薦問題（只管推薦問題） |
| enable_title | boolean | 是否自動產生聊天室標題與描述（預設 true） |
| update_title_only_once | boolean | 只在還沒命名時寫標題與描述（預設 false＝每輪重寫；`enable_title` 開時有效） |
| response_format | string | 回應格式：`markdown`／`html` |
| document_field_mapping | object | 文件欄位映射（索引欄位 → 給 AI 看的名稱） |
| selected_index | array | 預設檢索的索引列表 |

### 獲取預設配置
```
curl http://localhost:5555/api/config/
```
```json
{
  "json_data": {
    "chatroom_system_prompt": "",
    "product_system_prompt": "你是一個智能助手，專門回答用戶的問題。",
    "intension_system_prompt": "",
    "role": "智能助手",
    "model_name": "openai:gpt-5-mini",
    "search_selected_number": 8,
    "search_total_number": 16,
    "data_source_ratio": 0.0,
    "use_knowledge_mode": "strict",
    "enable_rerank": false,
    "memory_count": 5,
    "enable_suggest_questions": true,
    "enable_title": true,
    "update_title_only_once": false,
    "response_format": "markdown",
    "temperature": null,
    "timezone": "Asia/Taipei",
    "document_field_mapping": {
      "title": "標題",
      "content": "內容",
      "date": "日期",
      "category": "分類"
    },
    "selected_index": []
  },
  "error": false,
  "message": "成功獲取配置",
  "code": 0,
  "http_status": 200
}
```
（節錄，實際回傳完整 config 欄位。）

### 更新預設配置
```
curl -X PUT http://localhost:5555/api/config/ \
  -H "Content-Type: application/json" \
  -d '{
    "role": "AI助手",
    "model_name": "openai:gpt-5-nano",
    "search_selected_number": 10,
    "enable_suggest_questions": false
  }'
```

### 獲取所有配置列表
```
curl "http://localhost:5555/api/config/list?limit=1000&offset=0"
```
```json
{
  "json_data": [
    {
      "id": 1,
      "config_name": "default",
      "role": "智能助手",
      "model_name": "openai:gpt-5-mini",
      "created_at": "2024-01-01T10:00:00Z",
      "updated_at": "2024-01-01T12:00:00Z"
    },
    {
      "id": 2,
      "config_name": "stupid_assistant",
      "role": "不智能助手",
      "model_name": "openai:gpt-5-mini",
      "created_at": "2024-01-01T11:00:00Z",
      "updated_at": "2024-01-01T13:00:00Z"
    }
  ],
  "error": false,
  "message": "配置列表獲取成功",
  "code": 0,
  "http_status": 200
}
```
（節錄，實際每列含完整 config 欄位；無 `total_count`，請自行翻頁。）

### 根據名稱獲取特定配置
```
curl http://localhost:5555/api/config/stupid_assistant
```

### 創建新配置
`config_name` 不可使用保留字：`default`、`list`、`models`、`rerankers`、`builtin-tools`、`limits`。
```
curl -X POST http://localhost:5555/api/config/stupid_assistant \
  -H "Content-Type: application/json" \
  -d '{
    "role": "不智能助手",
    "model_name": "openai:gpt-5-mini",
    "product_system_prompt": "你是一個不智能助手，專門亂回問題。",
    "search_selected_number": 6,
    "search_total_number": 12,
    "enable_suggest_questions": false,
    "use_knowledge_mode": "strict"
  }'
```

### 更新特定配置
```
curl -X PUT http://localhost:5555/api/config/stupid_assistant \
  -H "Content-Type: application/json" \
  -d '{
    "model_name": "openai:gpt-5-nano",
    "search_selected_number": 8
  }'
```

### 刪除配置
不可刪除 `default`（400 `INVALID_OPERATION`）。
```
curl -X DELETE http://localhost:5555/api/config/stupid_assistant
```
```json
{
  "json_data": {
    "deleted_config": "stupid_assistant"
  },
  "error": false,
  "message": "配置已刪除: 'stupid_assistant'",
  "code": 0,
  "http_status": 200
}
```

## 回應信封與錯誤處理

除 SSE 串流外，所有端點都回同一種信封（成功時 `code` 為 0）：

```json
{
  "json_data": "",
  "error": true,
  "message": "錯誤訊息",
  "code": 1001,
  "http_status": 400
}
```

- **分流一律看 `code`，不要比對 `message`**：`message` 隨 `Accept-Language`（`zh-TW`／`en`／`ja`）變化，且常帶 `: <detail>` 後綴。
- 系統錯誤可能多帶 `error_id`，回報問題時附上即可在 chatbot 日誌找到原文。
- **HTTP status 不能從碼段推論**，請以下表為準（例如 2xxx 有 404／409／429／502／503）。
- 給終端使用者看時，5xx 建議統一顯示「系統錯誤，請稍後再試」。

### 錯誤代碼表

| Code | ErrorCode Name | HTTP | 說明 | 可重試 |
| ---- | -------------- | ---- | ---- | ------ |
| 0 | SUCCESS | 200 | 成功 | — |
| 1000 | GENERAL_ERROR | 400 | 保留碼，目前不會送出 | — |
| 1001 | INVALID_INPUT | 400 | 輸入值不合法（如內容長度、limit／offset、DSL 括號） | 否 |
| 1004 | INVALID_CONFIG | 400 | 配置內容不合法（含既存 config／聊天室凍結設定驗不過） | 否 |
| 1005 | INVALID_OPERATION | 400 | 不合法的操作（如刪除 `default` config） | 否 |
| 2000 | DATABASE_ERROR | 500 | 資料庫操作錯誤 | 是 |
| 2002 | CHAT_ROOM_NOT_FOUND | 404 | 聊天室不存在或已刪除 | 否 |
| 2003 | CHAT_LOG_NOT_FOUND | 404 | 聊天記錄不存在 | 否 |
| 2008 | CONFIG_ERROR | 500 | 配置處理錯誤 | 是 |
| 2009 | CONFIG_NOT_FOUND | 404 | 配置不存在 | 否 |
| 2010 | MCP_CONNECTION_ERROR | 500 | MCP Server 連線／啟動失敗 | 是 |
| 2012 | MCP_COMMAND_NOT_FOUND | 404 | MCP 指令找不到 | 否 |
| 2013 | MCP_SERVER_NOT_FOUND | 404 | MCP Server 不存在 | 否 |
| 2014 | GLOSSARY_TABLE_NOT_FOUND | 404 | 術語表不存在 | 否 |
| 2015 | SKILL_NOT_FOUND | 404 | skill 不存在 | 否 |
| 2016 | ALIAS_TABLE_NOT_FOUND | 404 | 別名表不存在 | 否 |
| 2017 | RAGAS_TASK_NOT_FOUND | 404 | RAGAS 評估任務不存在 | 否 |
| 2018 | SKILL_VERSION_NOT_FOUND | 404 | skill 存在，但該版本不存在 | 否 |
| 2019 | CONFIG_VERSION_NOT_FOUND | 404 | 配置存在，但該版本不存在 | 否 |
| 2020 | USAGE_LIMIT_UNAVAILABLE | 503 | 暫時無法確認使用額度 | 是 |
| 2021 | ALIAS_TABLE_VERSION_CONFLICT | 409 | 別名表已被他人修改（重讀後再送） | 是 |
| 2022 | RERANKER_CATALOG_UNAVAILABLE | 503 | 暫時無法取得重排序模型清單 | 是 |
| 2023 | LLM_CALL_FAILED | 502 | 呼叫語言模型失敗（分不出成因） | 視情況 |
| 2024 | LLM_CONTEXT_LENGTH_EXCEEDED | 502 | 輸入超過模型上下文長度，請縮短或開新對話 | 否 |
| 2025 | LLM_RATE_LIMITED | 429 | 模型供應商限流或額度不足 | 是（稍後） |
| 2026 | LLM_TIMEOUT | 502 | 呼叫語言模型逾時 | 是 |
| 2027 | LLM_UNAVAILABLE | 502 | 連不上模型供應商或供應商 5xx | 是 |
| 2028 | LLM_AUTH_FAILED | 502 | 供應商拒絕認證（API key 設定錯誤） | 否 |
| 2029 | LLM_REQUEST_REJECTED | 502 | 供應商拒絕請求（參數不支援、模型不存在等） | 否 |
| 2030 | MANAGER_LICENSE_INVALID | 503 | 檢索服務授權失效（需重新註冊序號） | 否 |
| 2031 | MANAGER_REQUEST_REJECTED | 502 | 檢索服務拒絕請求（權限、金鑰、格式） | 否 |
| 2032 | MANAGER_UNAVAILABLE | 503 | 檢索服務暫時無法使用 | 是 |
| 2999 | INTERNAL_SERVER_ERROR | 500 | 未預期的伺服器錯誤 | 是 |
| 3001 | INVALID_REQUEST_BODY | 422 | 請求體格式不符（型別錯、缺必填、未知欄位、超過長度） | 否 |
| 3002 | REQUEST_BODY_TOO_LARGE | 413 | 請求體超過 64 MiB | 否 |
| 4003 | DAILY_USAGE_LIMIT_EXCEEDED | 429 | 已達每日使用上限（隔日恢復） | 隔日 |

- **422 vs. 400**：422 是請求「形狀」不對（改欄位／型別），400 是形狀對但「值」不合法（改值）。
- **404**：呼叫端給錯 id，不是服務故障，不必當系統錯誤告警。
- SSE 的 `error` 塊 `data.code` 與本表共用同一套碼。

### 錯誤回應範例

#### 輸入驗證錯誤 (400)
```json
{
  "json_data": "",
  "error": true,
  "message": "無效的輸入",
  "code": 1001,
  "http_status": 400
}
```

#### 無效操作錯誤 (400)
```json
{
  "json_data": "",
  "error": true,
  "message": "這個配置名稱是保留字（'default'，以及設定 API 的靜態路徑 list／models／rerankers／builtin-tools／limits），請換一個名字: 'limits'",
  "code": 1005,
  "http_status": 400
}
```

#### 聊天室不存在錯誤 (404)
```json
{
  "json_data": "",
  "error": true,
  "message": "找不到聊天室: ID: 123",
  "code": 2002,
  "http_status": 404
}
```

#### 配置不存在錯誤 (404)
```json
{
  "json_data": "",
  "error": true,
  "message": "找不到配置: 'stupid_assistant'",
  "code": 2009,
  "http_status": 404
}
```

#### 請求體格式錯誤 (422)
```json
{
  "json_data": "",
  "error": true,
  "message": "請求內容格式不正確: human_content: Field required",
  "code": 3001,
  "http_status": 422
}
```

#### 每日使用上限 (429)
```json
{
  "json_data": "",
  "error": true,
  "message": "已達每日使用上限: 5/5",
  "code": 4003,
  "http_status": 429
}
```

#### 資料庫操作錯誤 (500)
```json
{
  "json_data": "",
  "error": true,
  "message": "資料庫操作錯誤",
  "code": 2000,
  "http_status": 500
}
```
