# GufoRAG Chat API 使用說明文件

給**產品後端**看的整合說明。chatbot 服務只在私有網路內被你的後端呼叫，所有端點都沒有身分閘門；
面向終端使用者的驗證、授權與內容剝除都在你這一層。範例程式在 `Program.cs`（C#，.NET 8）。

所有 URL 以 reverse proxy 的位址為例：`http://localhost:5555`。

## 整合前必讀的八件事

1. **回應一律是同一個信封。** 成功與失敗都是 `{json_data, error, message, code, http_status}`，HTTP 狀態碼永遠等於 `http_status`。連框架自己產生的 404（路徑打錯）與 405（方法不對）也走這個信封。
2. **分流一律看 `code`，不要比對 `message`。** `message` 隨 `Accept-Language` 變（`zh-TW`／`en`／`ja`），而且常帶 `: <detail>` 後綴。
3. **未知欄位一律 422。** 請求體多送一個沒宣告的鍵、query string 帶一個端點沒宣告的參數（連 cache-buster 也算），都回 422 `INVALID_REQUEST_BODY`（3001），不會被靜默忽略。
4. **422 是形狀錯、400 是值錯。** 型別錯、缺必填、超過欄位上界是 422，要改請求的形狀；引用了不存在的資源、業務界線不合是 400，要改值。
5. **找不到資源回 404，不是 500。** `CHAT_ROOM_NOT_FOUND`、`CHAT_LOG_NOT_FOUND`、`CONFIG_NOT_FOUND` 等 not-found 家族一律 404。每日額度用完回 **429**，額度服務暫時不可用回 **503**。
6. **清單端點一律回一頁。** 不帶 `limit` 拿到的是預設 100 筆，不是全部；`limit` 上限 1000、`offset` 上限 100000，超過回 400 而不是靜默夾限。要全部就自己翻頁。
7. **聊天室在建立當下凍結整份設定。** 之後改 config 不影響既有聊天室的續談；`chat_room` 塊與房間列表回的都是凍結值，並帶 `config_name`＋`config_version` 說明這間房用的是哪一版。
8. **設定的讀寫不能原樣 round-trip。** `GET /api/config/…` 的回應多一個 `version_no`，而寫入模型禁止未知鍵，整包 PUT 回去會 422。PUT 是局部更新，只送要改的欄位即可。

## 最短整合流程

```
GET  /api/config/models                    → 拿模型目錄，據此畫模型下拉
POST /api/config/{name}                    → 建一份設定（或直接用 default）
POST /api/chat/chatbot   (chat_room_id=null) → 第一輪，從 chat_room 塊拿到 id 與 latest_chat_log_id
POST /api/chat/chatbot   (chat_room_id=id)   → 續談
GET  /api/chat/chatrooms/{id}/chatlogs     → 讀該房歷史（分頁）
POST /api/chat/chatlogs/{log_id}/rating    → 評價
```

## 語言協商（`Accept-Language`）

支援 `zh-TW`／`en`／`ja`，預設 `zh-TW`。大小寫不敏感，地區變體收斂到基底（`en-US`→`en`、`zh-CN`→`zh-TW`），`q` 權重被忽略，取標頭裡由左到右第一個支援的語言。認不出來就落回 `zh-TW`，不會 400。

它只影響**系統訊息**（錯誤 `message`、SSE 的 `status.content` 與 `error.content`），不影響 AI 回答的語言。

---

## 聊天對話 API（SSE 串流）

### URL
```
POST http://localhost:5555/api/chat/chatbot
Content-Type: application/json
```

### 請求體

未知欄位一律 422。「上限」是結構性上界（超過回 422）；業務規則另有其處，例如 `human_content` 的 1 到 2000 字是 400。

| 欄位 | 型別 | 上限 | 說明 |
| --- | --- | --- | --- |
| chat_room_id | int? | 1 到 2147483647 | 聊天室 ID。**只有 `null`（或不送）才是新建**；`0` 與負數回 422 |
| chat_log_id | int? | 1 到 2147483647 | 在某一則之後續談（分支）。必須屬於 `chat_room_id` 那間房，否則 404 |
| human_content | string | 100000 字 | 使用者輸入。**業務上限 strip 後 1 到 2000 字**，不符回 400 |
| config_name | string? | 100 字 | 設定名稱，預設 `"default"`。續談時以房間凍結的 `config_name` 為準，請求值被忽略 |
| user_id | string? | 255 字 | 呼叫端自填的標籤，供列表過濾；建室當下凍結 |
| tenant_id | string? | 255 字 | 同上。**不是安全邊界**，chatbot 沒有租戶概念 |
| tag | string? | 50 字 | 通用標籤，寫進房間與這一則紀錄，供歷史過濾；建室當下凍結 |
| prompt_version | int? | 0 到 2147483647 | 稽核用：本次回答用的帳號提示詞版本號，寫進這一則紀錄 |
| selected_index | string[]? | 500 項，每項 200 字 | 檢索索引範圍；非空才覆蓋 config（送 `[]` 等於沒送）。空＝搜尋全部索引 |
| index_tiers | string[][]? | 20 層，每層 500 項 | 分層檢索（優先級由前到後）；送了就覆蓋 config，`[]` 是顯式清掉分層 |
| intent_enabled | bool? | | 每層是否先用意圖向量縮到最相關的 index。agent 模式下不生效 |
| intent_tiers | bool[]? | 20 項 | 逐層意圖開關，與 `index_tiers` 對齊，長度不符回 422 |
| single_prompt | string? | 20000 字 | 本次專用的系統提示詞，接在五層提示詞之後；不存帳號，只落在這一則紀錄 |
| dsl | string? | 4000 字 | 篩選 DSL，原樣傳給 manager_backend 並與系統解析的條件以 `and` 併接。**括號或引號不平衡回 400**，擋在佔用配額之前 |
| document_ids | string[]? | 1000 項，每項 256 字 | 限縮到這些文件。與 `dsl` 同族：建室凍結；續談帶了以請求為準（本輪生效、不寫回房間），沒帶沿用凍結值 |
| intent_context | string? | 2000 字 | 呼叫端的意圖分類結果，必須是 JSON 物件的字串；只交給輸出規則用，不落庫 |

三個整數欄的上界就是資料表 `INTEGER` 的欄寬。`GET /api/config/limits` 的 `chat` 分組把它們投影出來，組請求體之前先擋，不要等落庫才炸。

### 請求範例
```json
{
  "chat_room_id": null,
  "chat_log_id": null,
  "human_content": "請問什麼是人工智慧？",
  "config_name": "default",
  "user_id": "user123",
  "tenant_id": "acme",
  "tag": "web",
  "selected_index": ["technical_docs", "faq_docs"],
  "dsl": "$privileges in [10, 20] and $containsAny in [1, 2, 3]"
}
```

```
curl -N -X POST http://localhost:5555/api/chat/chatbot \
  -H "Content-Type: application/json" \
  -H "Accept-Language: zh-TW" \
  -d '{"chat_room_id": null, "chat_log_id": null, "human_content": "請問什麼是人工智慧？", "config_name": "default", "user_id": "user123"}'
```

### 這支端點不保證回 SSE

下列情況在串流開始**之前**就回普通 JSON 信封（`Content-Type: application/json`），而且**不佔用量配額**。呼叫端先看狀態碼與 `Content-Type` 再決定要不要掛 SSE 解析器，否則症狀是「串流一個事件都沒有就結束」。

| 情況 | HTTP | code |
| --- | --- | --- |
| 請求體形狀錯（未知鍵、型別錯、id 為 0 或負數、超過上限、`intent_tiers` 對不齊） | 422 | 3001 INVALID_REQUEST_BODY |
| `chat_room_id` 不存在或已刪除；`chat_log_id` 不屬於該房 | 404 | 2002 CHAT_ROOM_NOT_FOUND |
| `chat_log_id` 不存在或已刪除 | 404 | 2003 CHAT_LOG_NOT_FOUND |
| `config_name` 不存在（只有 `default` 會自動建立） | 404 | 2009 CONFIG_NOT_FOUND |
| config 儲存值或房間凍結快照不符現行界線 | 400 | 1004 INVALID_CONFIG，`detail` 指名欄位 |
| `human_content` strip 後為空或超過 2000 字；`dsl` 括號引號不平衡 | 400 | 1001 INVALID_INPUT |
| agent 模式且一顆工具都沒有 | 400 | 1004 INVALID_CONFIG |
| 每日額度用完 | 429 | 4003 DAILY_USAGE_LIMIT_EXCEEDED，`detail` 為 `已用/上限` |
| 額度服務不可用 | 503 | 2020 USAGE_LIMIT_UNAVAILABLE |
| 未預期例外 | 500 | 2999 INTERNAL_SERVER_ERROR |

輸入合規閘擋下**不是**早退：仍回 200 SSE，形狀見下方「被合規閘擋下」。

### SSE 格式

每一個事件一行 `data: {JSON}`，後接空行。沒有 `event:`、`id:`、註解行。每一顆 chunk 的外殼四鍵固定：

```json
{"session_id": null, "chunk_type": "<type>", "content": "<字串>", "data": { ... }}
```

`session_id` 恆為 `null`，請忽略。

| chunk_type | 說明 | 發送時機 |
| --- | --- | --- |
| status | 階段旁白或結構化步驟事件 | 每次階段切換 |
| thinking | 模型的推理文字（支援的模型才有） | 生成過程中 |
| message | AI 回答（逐字串流） | 生成回答時 |
| agent_tool_call | agent 呼叫工具（工具名與參數） | agent 模式 |
| agent_tool_result | 工具回傳結果摘要 | agent 模式 |
| chat_room | 聊天室完整資訊（含來源卡、推薦問題、用量） | 對話完成後一次 |
| error | 錯誤，**終止塊** | 處理中發生錯誤 |
| end | 結束，**終止塊** | 串流結束 |

`error` 與 `end` 只會出現其中一個；發了 `error` 就不會再有 `end`。收到任一個就收線。

`status`、`thinking`、`agent_tool_call`、`agent_tool_result` 是過程資訊，整批忽略不影響答案；`message` 的 `content` 逐字累加就是答案全文。

#### message
```
data: {"session_id": null, "chunk_type": "message", "content": "人工智慧是", "data": {"content": "人工智慧是", "timestamp": "2026-09-04T03:12:45.120334+00:00"}}
```

設定啟用了輸出合規閘、出口替換或輸出規則時，`message` 不再逐字送：整段答案攢滿後在 `chat_room` 之前一次送出**一顆** `message`。消費端的累加邏輯不必改，但不能假設一定會有多顆。

#### thinking
```
data: {"session_id": null, "chunk_type": "thinking", "content": "使用者在問退貨期限…", "data": {"content": "使用者在問退貨期限…", "node_name": "respond_generate", "timestamp": "2026-09-04T03:12:44.900000+00:00"}}
```

與 `message` 分開串流，前端應與答案區隔顯示（灰底、可折疊），不要併進答案。只有 `GET /api/config/models` 裡 `reasoning_options` 非空或 `adaptive_thinking` 為 true 的模型才會有；沒有就是沒有，視為可選。

#### status

`content` 是**顯示字**，隨 `Accept-Language` 變。分流一律比 `data.status_code`，不要比 `content`。

`status` 有兩種 `data`，判準是有沒有 `step_id`：

- **旁白**：只帶 `status_code`（接下來要開始的階段）、`status`、`node_name`、`timestamp`。不進時間軸。
- **步驟事件**：另帶 `step_id`（剛跑完的是誰）、`step_status`（`running`／`completed`／`failed`／`skipped`）、`label`、`duration_ms`（量得到才有；`skipped` 不會有）、`timestamp`（那一步結束的時刻）。agent 子圖的事件另有 `seq`（排序請用它，不要用 `timestamp`）與 `tools`。

```
data: {"session_id": null, "chunk_type": "status", "content": "生成回應", "data": {"node_name": "grounding", "status": "status", "status_code": "stream_chat_response", "timestamp": "2026-09-04T03:12:44.100000+00:00", "step_id": "grounding", "step_status": "completed", "label": "可回答性判定", "duration_ms": 812, "verdict": "generate", "reason": "llm"}}
```

常見的 `step_id`：`input_gate`、`init_chat_room`、`task_assignment`、`get_search_results`、`grounding`、`stream_chat_response`、`respond_no_answer`、`generate_recommendations`、`finalize_chat_session`、`output_gate`；agent 模式另有 `agent_loop`、`retrieve`、`cap_finalize`、`respond_generate`、`mcp_tool_load`。合規閘擋下時該步驟 `step_status` 為 `failed`、`verdict` 為 `blocked` 並帶 `blocked_rules`。同一組鍵逐筆落進 `chat_logs.step_events`，可事後用 `/trace` 端點重播。

#### agent_tool_call、agent_tool_result（agent 模式）
```
data: {"session_id": null, "chunk_type": "agent_tool_call", "content": "呼叫工具: search_knowledge_base", "data": {"tool": "search_knowledge_base", "tool_call_id": "call_8f2a", "args": {"query": "退貨期限"}, "timestamp": "2026-09-04T03:12:41.000000+00:00"}}
data: {"session_id": null, "chunk_type": "agent_tool_result", "content": "工具 search_knowledge_base 執行完成", "data": {"tool": "search_knowledge_base", "tool_call_id": "call_8f2a", "result_preview": "[[1]] 退貨須於 7 日內…", "timestamp": "2026-09-04T03:12:42.000000+00:00", "source_index": 1, "server": "builtin", "args": {"query": "退貨期限"}}}
```

`result_preview` 是餵給模型的那份工具輸出；設定啟用輸出政策或出口替換時會被換成佔位字。

#### chat_room

對話完成後發一次。`data` 的欄位＝這間房**凍結的整份設定**（[配置參數說明](#配置參數說明)那張表的每一欄，除了刻意不外送的 `policy_word_filters`、`policy_topics`、`output_replacements`、`output_rules`、`welcome_message`、`ragas_model_name`）加上下表：

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| id | int | 聊天室 ID。新建時這裡是第一次拿到它的地方 |
| title | string | 標題（由推薦問題那次 LLM 呼叫產生）。該房有任一輪被輸出閘擋下時為 `""` |
| description | string? | 描述，同上情況為 `null` |
| status | string | `active` |
| user_id、tenant_id、tag | string? | 建室當下凍結的標籤 |
| config_name | string | 本房用的設定名稱 |
| config_version | int | 本房凍結的設定版本號，配合 `GET /api/config/{name}/versions/{no}` 可取回當時內容 |
| dsl | string? | 本輪生效的 DSL |
| document_ids | string[]? | 本輪生效的文件限縮。`null`＝沒有限縮到特定文件 |
| active_chain_end_id | int? | 本房目前作用中對話鏈的末端 `chat_log_id`（分支續談時會變） |
| chat_logs_count | int? | 本房對話則數（真的 COUNT）。查不動時回 `null`，不是 0 |
| latest_chat_log_id | int | 本輪這一則的 `chat_log_id`。評價、trace、raw 都用它 |
| grounding_no_answer | bool | 本輪是否走了「誠實查無」（素材不足以支撐答案）。這是本輪的結論，不是設定 |
| intent_index | string? | 意圖路由命中的資料集；沒跑或沒命中為 `null`；agent 模式恆 `null` |
| suggest_questions | string[] | 推薦問題；`enable_suggest_questions=false` 時為 `[]` |
| search_results | object[] | 本輪來源卡，格式見下 |
| usage | object? | `{input_tokens, output_tokens, total_tokens, model}`；供應商有回報才另帶 `cache_read_input_tokens`、`cache_creation_input_tokens`。供應商未回報 token 時整欄 `null`。鍵集開放，用 `.get()` 取 |
| latency_ms | int | 本輪端到端耗時（含配額、輸入閘、直答判定） |
| ttft_ms | int? | 首個 token 延遲。QA 直答那一輪等於 `latency_ms` |
| answer_source | string | **只在 QA 直答那一輪出現**，值固定 `"qa_direct"`；一般生成沒有這個鍵 |
| created_at、updated_at、timestamp | string | ISO 8601，UTC |

```json
data: {"chunk_type": "chat_room", "content": "", "data": {
  "id": 312, "title": "退貨期限", "description": "使用者詢問退貨期限與流程", "role": "智能助手", "status": "active",
  "model_name": "openai:gpt-5-mini", "reasoning_effort": "low",
  "user_id": "user123", "tenant_id": "acme", "tag": "web", "config_name": "default", "config_version": 7,
  "selected_index": ["technical_docs", "faq_docs"], "dsl": "$privileges in [1]", "document_ids": null,
  "search_selected_number": 8, "search_total_number": 16, "data_source_ratio": 0.0,
  "use_knowledge_mode": "strict", "enable_rerank": false, "reranker_name": "llm_reranker",
  "memory_count": 5, "response_format": "markdown", "enable_suggest_questions": true, "temperature": 0.0,
  "search_mode": "traditional", "agent_max_iterations": 5,
  "active_chain_end_id": 9821, "chat_logs_count": 3, "latest_chat_log_id": 9821,
  "grounding_no_answer": false, "intent_index": null,
  "suggest_questions": ["退貨需要附發票嗎？", "運費由誰負擔？"],
  "search_results": [
    {"doc_name": "退貨政策.pdf", "document_id": "a1b2c3", "chunk_index": 2, "data_source": "policy",
     "index": "technical_docs", "search_mode": "hybrid", "last_modified": "2026-05-01T00:00:00+00:00",
     "score": 0.83, "source_no": 1,
     "document": {"title": "退貨政策", "content": "退貨須於 7 日內…", "search": "退貨須於 7 日內…"}}
  ],
  "usage": {"input_tokens": 1834, "output_tokens": 212, "total_tokens": 2046, "model": "openai:gpt-5-mini"},
  "latency_ms": 6120, "ttft_ms": 2310,
  "created_at": "2026-09-01T08:00:00+00:00", "updated_at": "2026-09-04T03:12:45+00:00", "timestamp": "2026-09-04T03:12:45.500000+00:00"
}}
```

（上例為節錄，實際 `data` 還有整份凍結設定的其餘欄位；線上每個 frame 都是單行。）

#### end
```
data: {"session_id": null, "chunk_type": "end", "content": "", "data": {"status": "completed", "timestamp": "2026-09-04T03:12:46.000000+00:00"}}
```

#### error
```
data: {"session_id": null, "chunk_type": "error", "content": "伺服器內部錯誤", "data": {"status": "error", "timestamp": "2026-09-04T03:12:46.000000+00:00"}}
```

收到 `error` 就停止處理並顯示 `content`，之後不會有 `end`。

#### 被合規閘擋下

設定有 `policy_word_filters` 或 `policy_topics` 時，輸入或輸出可能被擋。兩種情況 `end` 的 `data` 都另帶 `blocked: true` 與 `detections`（命中的條目），沒被擋時**不放這兩個鍵**。

- **輸入被擋**：`status`（`step_id: input_gate`、`step_status: failed`、`verdict: blocked`）→ `message`（拒答文字）→ `end`。**沒有 `chat_room` 塊**；若已建了房，`end.data` 多一個 `chat_room_id`，請用它避免留下孤兒房。
- **輸出被擋**：`message` 為擋下訊息；`chat_room` 仍有，但 `suggest_questions` 與 `search_results` 為 `[]`、`title` 為 `""`；`end` 帶 `blocked` 與 `detections`。

```
data: {"session_id": null, "chunk_type": "end", "content": "", "data": {"status": "completed", "timestamp": "...", "blocked": true, "detections": [{"stage": "input", "kind": "word", "name": "某禁詞", "action": "block", "refusal": ""}], "chat_room_id": 312}}
```

`detections` 帶的是合規設定的原文（禁詞、主題名），是給後端做統計稽核用的，**轉發給終端使用者之前請剝除**。另有一種非合規條目 `{kind: "truncated", name: "answer_buffer_truncated", action: "record"}`，代表答案超過出口閘緩衝上限而被截短。

### 檢索結果格式（search_results）

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| doc_name | string | 文件名稱 |
| document_id | string | 文件唯一識別碼 |
| chunk_index | int | 分塊索引，從 0 開始 |
| data_source | string | 資料來源 |
| index | string | 所屬索引 |
| search_mode | string | 檢索策略：`weaviate_only`／`gufonet_only`／`hybrid`（不是 traditional／agent） |
| last_modified | string? | 最後修改時間。上游沒給或解析不出來就是 **`null`**，不會被填成現在；型別請宣告成可空 |
| score | float | 相關性分數 |
| source_no | int? | 本輪來源編號。答案裡的 `[[N]]` 指的就是 `source_no == N` 這張卡；`null`＝模型沒看過這張卡（重排序候選池），不會被引用 |
| document | object | 索引的原始欄位物件，**鍵集隨索引而異**（對應匯入時放進 content 的欄位）；`search` 欄一定有，是命中的文字段落 |

`[[N]]` 是否合法只有一個判準：`N` 出現在本輪 `search_results` 的 `source_no` 集合裡。不要自己推算，兩個模式的編號機制不同。服務只偵測懸置引用並記日誌，不改寫答案；要不要剝掉懸置標記由你決定。

---

## 聊天室 API

### 列出聊天室（分頁）
```
GET http://localhost:5555/api/chat/chatrooms
```

| 參數 | 型別 | 說明 |
| --- | --- | --- |
| limit | int | 預設 100，上限 1000；`<=0` 或超過上限回 400 |
| offset | int | 預設 0，上限 100000；負數或超過回 400 |
| tenant_id | string | 精確相等過濾；未帶或空字串＝不套用。上限 255 字，超過 422 |

回**裸清單**（沒有 `total_count`），依建立時間新到舊。每一列＝該房凍結的整份設定（同 `chat_room` 塊）加 `tenant_id`、`tag`、`config_name`、`config_version`、`chat_logs_count`、`created_at`、`updated_at`。有任一輪被輸出閘擋下的房，`title` 為 `""`、`description` 為 `null`。

```
curl "http://localhost:5555/api/chat/chatrooms?limit=100&offset=0"
curl "http://localhost:5555/api/chat/chatrooms?tenant_id=acme&limit=50"
```

### 依使用者列出聊天室
```
GET http://localhost:5555/api/chat/chatrooms/user/{user_id}
```

`limit`／`offset` 同上。與上一支不同，這一支的 `json_data` 是**信封**：

```json
{
  "json_data": {
    "chat_rooms": [ { "...同上每一列..." } ],
    "total_count": 5,
    "returned_count": 1,
    "user_id": "user123"
  },
  "error": false, "message": "操作成功", "code": 0, "http_status": 200
}
```

`total_count` 是符合條件的真總數（排除已刪除的房），用它判斷何時翻完。

### 刪除聊天室
```
DELETE http://localhost:5555/api/chat/chatrooms/{chat_room_id}
```

軟刪除：房間 `status` 設成 `deleted`，該房所有紀錄標記刪除，之後所有讀取端點對它們回 404。重複刪第二次拿到的是 404，不是 200。

```json
{"json_data": {"chat_room_id": 1, "status": "deleted"}, "error": false, "message": "操作成功", "code": 0, "http_status": 200}
```

---

## 聊天記錄 API

三支端點（單室清單、全域清單、單筆）共用同一個序列化器，每一則的欄位形狀一致，見[chat_log 欄位表](#chat_log-欄位表)。`null` 一律是真的沒有（該輪沒跑到），不會被偽造成 `[]` 或 `{}`。

### 單一聊天室的紀錄（分頁）
```
GET http://localhost:5555/api/chat/chatrooms/{chat_room_id}/chatlogs
```

| 參數 | 型別 | 說明 |
| --- | --- | --- |
| limit | int | 預設 100，上限 1000。**不帶拿到的是一頁，不是整室** |
| offset | int | 預設 0，上限 100000 |
| preview_chars | int | 長文欄位的回傳長度上限。預設 `0`＝不截。作用於 `thinking_content`、`tool_activity[].result_preview`、`thinking_by_node`、`search_results[]` 的內容欄；`ai_content`／`human_content` 不截 |

房間不存在或已刪回 404 `CHAT_ROOM_NOT_FOUND`。回裸清單（沒有 `total_count`），依時間舊到新。

```
curl "http://localhost:5555/api/chat/chatrooms/1/chatlogs?limit=100&offset=0&preview_chars=500"
```

### 全域紀錄清單（過濾、分頁）
```
GET http://localhost:5555/api/chat/chatlogs
```

| 參數 | 型別 | 說明 |
| --- | --- | --- |
| user_id | string | 精確相等。上限 255 字 |
| tenant_id | string | 精確相等。上限 255 字 |
| tag | string | 精確相等。上限 50 字 |
| exclude_tags | string | 排除標籤，可重複或逗號分隔（`?exclude_tags=a,b`）。展開後最多 50 項；沒有標籤的紀錄不受影響 |
| keyword | string | 對 `human_content`／`ai_content` 的關鍵字。**至少 2 字元**（過短回 400），上限 2000 |
| rating_type | string | `positive`／`negative`／`unrated`（＝尚未評價）。打錯字回 400 並列出可用值 |
| answer_source | string | `qa_direct`／`generated`（＝一般生成）。打錯字回 400 |
| prompt_version | int | 精確相等 |
| config_name | string | 精確相等。上限 100 字 |
| config_version | int | 精確相等 |
| config_version_gte、config_version_lt | int | 版本區間 `[gte, lt)`，矛盾回 400 |
| start_time、end_time | string | ISO 8601；格式錯回 400；`end` 必須 ≥ `start` |
| limit | int | 預設 100，上限 1000 |
| offset | int | 預設 0，上限 100000 |
| preview_chars | int | 同上一支 |

字串欄的長度上限就是 DB 欄寬，超界回 422；值不對（limit 超界、keyword 過短、時間格式錯）回 400。

```
curl "http://localhost:5555/api/chat/chatlogs?user_id=user123&limit=20"
curl "http://localhost:5555/api/chat/chatlogs?start_time=2026-01-01T00:00:00Z&end_time=2026-01-31T23:59:59Z"
curl "http://localhost:5555/api/chat/chatlogs?rating_type=negative&answer_source=generated"
curl "http://localhost:5555/api/chat/chatlogs?exclude_tags=qatest,ab_test&limit=50&offset=100"
```

回應：

```json
{
  "json_data": {
    "chat_logs": [ { "...見欄位表..." } ],
    "total_count": 150,
    "returned_count": 20,
    "limit": 20,
    "offset": 0,
    "filters_applied": {
      "user_id": "user123", "tenant_id": null, "tag": null, "exclude_tags": null, "keyword": null,
      "rating_type": null, "answer_source": null, "prompt_version": null, "config_name": null,
      "config_version": null, "config_version_gte": null, "config_version_lt": null,
      "start_time": null, "end_time": null
    }
  },
  "error": false, "message": "操作成功", "code": 0, "http_status": 200
}
```

`filters_applied` 回的是**實際套用的值**：字串過濾器帶了但值是空，回 `null`（等於沒帶）；`exclude_tags` 回正規化後的清單；`config_name` 帶空字串會真的比對空字串，所以照抄 `""`。用它確認你送的過濾有沒有生效。`total_count` 套用同一組過濾。

### 單筆紀錄
```
GET http://localhost:5555/api/chat/chatlogs/{chat_log_id}
```

`preview_chars` 同上。不存在或已刪回 404 `CHAT_LOG_NOT_FOUND`；所屬房已刪回 404 `CHAT_ROOM_NOT_FOUND`。

在共用欄位之上，這一支**多回**稽核脈絡：`chat_room_title`、`user_id`、`tenant_id`、`system_prompt`、`is_intension`、`model_name`、`search_total_number`、`search_selected_number`、`document_field_mapping`。其中 `system_prompt` 是提示詞原文，面向終端使用者的介面不要直接轉發整個物件。

### chat_log 欄位表

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| id | int | 紀錄 ID |
| chat_room_id | int | 所屬聊天室 |
| previous_chat_log_id | int? | 前一則（對話鏈；分支時同一則可有多個後繼） |
| human_content | string | 使用者輸入 |
| ai_content | string? | AI 回答。讀取時重演出口處理（遮蔽、替換、輸出規則）；原文走 `/raw` |
| thinking_content | string? | 推理內容（模型有回推理才有） |
| human_time | string | 送出時間（ISO 8601，UTC） |
| ai_time | string? | 回答完成時間 |
| suggest_questions | string[]? | 推薦問題 |
| search_results | object[]? | 來源卡，格式同上 |
| language | string? | 本輪偵測到的對話語言。`null`＝這一輪沒做語言偵測 |
| query_start_time、query_end_time | string? | 從問句解析出的**資料查詢日期範圍**，不是處理時間 |
| keywords、filenames | string[]? | 從問句提取的關鍵字與檔名 |
| question | string? | 補全成完整句子的問題 |
| tag | string? | 該則的標籤 |
| prompt_version | int? | 本則所用的帳號提示詞版本號 |
| single_prompt | string? | 本則所用的單次提示詞（正式回答為 null） |
| rating_type、rating_feedback、rating_time | string? | 評價；未評價為 null |
| input_tokens、output_tokens、total_tokens | int? | 本輪 token 用量；供應商未回報為 null |
| ttft_ms | int? | 首個 token 延遲。被輸入閘擋下的那一輪為 null |
| request_started_at、request_ended_at | string? | 請求處理起訖時刻（對時用；時長請讀 `ttft_ms`） |
| blocked_by | string? | 被哪一道合規閘擋下：`input_policy`／`output_policy`；未被擋為 null |
| policy_detections | object[]? | 命中的合規條目 |
| answer_source | string? | `qa_direct`；一般生成為 null |
| search_mode | string? | 本輪的檢索模式：`traditional`／`agent` |
| step_events | object[]? | 步驟事件（前 200 筆＋截斷標記）；完整值走 `/trace` |
| tool_activity | object[]? | 工具呼叫軌跡（前 50 筆＋截斷標記） |
| thinking_by_node | object? | 逐節點推理內容 `{節點名: 文字}` |

### 完整執行軌跡與未遮蔽原文
```
GET http://localhost:5555/api/chat/chatlogs/{chat_log_id}/trace
GET http://localhost:5555/api/chat/chatlogs/{chat_log_id}/raw
```

- `/trace` 回 `{id, chat_room_id, step_events, tool_activity, thinking_by_node}`，筆數與長度不夾限（不吃 `preview_chars`），仍套讀取端遮蔽。後台「載入完整軌跡」用。
- `/raw` 回 `{id, chat_room_id, masked_fields, ai_content, thinking_content, search_results, suggest_questions, keywords, tool_activity, thinking_by_node, step_events, room_title, room_description}`，**不遮蔽、不截斷**。`masked_fields` 列出正規路徑會動到的欄名。這支是唯一的旁路，要不要暴露給誰由你逐入口決定。

### 提交評價
```
POST http://localhost:5555/api/chat/chatlogs/{chat_log_id}/rating
```

| 欄位 | 型別 | 必填 | 說明 |
| --- | --- | --- | --- |
| rating_type | string | 是 | `positive`／`negative` |
| feedback | string? | 否 | 上限 2000 字，超過 422 |

未知欄位 422（`feedback` 打成 `feedbck` 不會被靜默丟掉）。同一則只保留最後一次評價。

```
curl -X POST http://localhost:5555/api/chat/chatlogs/1/rating \
  -H "Content-Type: application/json" \
  -d '{"rating_type": "positive", "feedback": "回答很有幫助"}'
```

```json
{
  "json_data": {
    "chat_log_id": 1, "tag": "web", "prompt_version": 3, "single_prompt": null,
    "rating_type": "positive", "rating_feedback": "回答很有幫助", "rating_time": "2026-09-04T10:05:00+00:00"
  },
  "error": false, "message": "評價提交成功", "code": 0, "http_status": 200
}
```

### 使用次數狀態
```
GET http://localhost:5555/api/chat/usage-status
```

回當天的問答次數狀態，每日上限來自 manager_backend。

| 欄位 | 型別 | 說明 |
| --- | --- | --- |
| current_usage | int | 今天已用次數。無限制時固定 0 |
| max_usage | int | 每日上限。`0`＝無限制 |
| remaining_usage | int | 剩餘次數。`-1`＝無限制；超額時夾在 0 |
| is_allowed | bool | 現在是否還能問 |
| is_unlimited | bool | 是否無限制 |

判斷「還能不能問」請看 `is_allowed`，不要對 `remaining_usage` 做算術。拿不到上限時回 503，不放行。

---

## 配置管理 API

### 配置參數說明

寫入模型禁止未知鍵（打錯欄位名回 422，不會靜默忽略）。PUT 是**局部更新**，只覆寫有帶到的欄位。每次寫入自動存一份版本快照。下表列產品整合常用的欄位；完整欄位以 `GET /api/config/` 的回應為準，界線以 `GET /api/config/limits` 為準。

#### 提示詞與人設
| 參數 | 型別 | 預設 | 說明 |
| --- | --- | --- | --- |
| product_system_prompt | string | "你是一個智能助手，專門回答用戶的問題。" | 產品系統提示詞（上限 20000 字） |
| chatroom_system_prompt | string | "" | 聊天室系統提示詞 |
| intension_system_prompt | string | "" | 意圖分析提示詞（只有傳統模式讀） |
| role | string | "智能助手" | 機器人身份（上限 100 字） |
| welcome_message | string | "你好，請問有甚麼問題需要我幫你解答的?" | 開場白，chatbot 不消費，純投影給前台 |

#### 模型
| 參數 | 型別 | 預設 | 說明 |
| --- | --- | --- | --- |
| model_name | string | "openai:gpt-5-mini" | 主回答模型。`provider:model` 形式；`openai:`／`anthropic:` 必須在目錄內，`ollama:`／`vllm:` 只驗前綴。目錄看 `GET /api/config/models` |
| reasoning_effort | string | "" | 思考深度。必須在該模型的 `reasoning_options` 內；空＝模型預設；模型沒有這個旋鈕時必須留空，否則 400 |
| temperature | float | 0.0 | 0 到 1。`omit_temperature` 為 true 的模型不送 |
| model_name_intent、model_name_judge、model_name_tools、model_name_skill、model_name_recommend | string | "" | 分組模型，空＝繼承主模型；各有對應的 `reasoning_effort_*` |

#### 檢索
| 參數 | 型別 | 預設 | 說明 |
| --- | --- | --- | --- |
| search_mode | string | "traditional" | `traditional`／`agent` |
| search_selected_number | int | 8 | 放進上下文的來源卡數，1 到 100 |
| search_total_number | int | 16 | 向 manager 取回的筆數，1 到 100。必須 ≥ `search_selected_number`，局部更新也擋 |
| data_source_ratio | float | 0.0 | 0＝純向量，1＝純關鍵字 |
| use_knowledge_mode | string | "strict" | `none`／`assist`／`strict`。**兩種模式都讀它**（agent 側的注入點在 respond 節點，租戶自訂 respond 提示詞時照樣附加）。它**不參與 QA 直答的判定**——`none` 與直答同時開著是合法組合，關直答只有 `qa_direct_enabled` 一條路 |
| enable_rerank | bool | false | 是否重排序 |
| reranker_name | string | "llm_reranker" | `llm_reranker`／`bge_reranker`／`jina_reranker`，目錄看 `GET /api/config/rerankers` |
| selected_index | string[] | [] | 索引清單。空＝搜尋全部索引 |
| index_tiers | string[][] | [] | 分層檢索 |
| intent_enabled、intent_tiers | bool、bool[] | false、[] | 逐層意圖縮索引（只有傳統模式讀） |
| document_field_mapping | object | {"title":"標題","content":"內容","date":"日期","category":"分類"} | 文件欄位映射 |

#### 對話行為
| 參數 | 型別 | 預設 | 說明 |
| --- | --- | --- | --- |
| memory_count | int | 5 | 帶進上下文的歷史則數，0 到 100 |
| enable_suggest_questions | bool | true | 推薦問題與聊天室標題 |
| update_title_only_once | bool | false | 只在第一輪寫標題 |
| response_format | string | "markdown" | `markdown`／`html` |
| enable_citation | bool | false | 答案加 `[[N]]` 來源標註 |
| timezone | string | "Asia/Taipei" | IANA 時區 |
| agent_max_iterations | int | 5 | agent 最大工具輪數，1 到 20 |

進階欄位（合規閘 `policy_*`、出口替換 `output_replacements`、輸出規則 `output_rules`、術語表 `glossary_*`、別名表 `alias_*`、skill `skill_ids`、QA 直答 `qa_direct_*`、可回答性閘 `answerability_gate_mode`／`grounding_score_floor`、內建工具 `enabled_builtin_tools`／`builtin_tool_overrides`、MCP `mcp_server_ids`）預設全部關閉或為空，不設就不啟動。它們的語意與界線見 chatbot 服務自己的 README。

### 取得預設配置
```
GET http://localhost:5555/api/config/
```

`default` 不存在時自動建立。回應 `json_data` 是整份設定（全部欄位）**加一個 `version_no`**（目前版本號）。

```json
{
  "json_data": {
    "product_system_prompt": "你是一個智能助手，專門回答用戶的問題。",
    "role": "智能助手",
    "model_name": "openai:gpt-5-mini",
    "reasoning_effort": "",
    "search_selected_number": 8,
    "search_total_number": 16,
    "data_source_ratio": 0.0,
    "use_knowledge_mode": "strict",
    "enable_rerank": false,
    "reranker_name": "llm_reranker",
    "memory_count": 5,
    "enable_suggest_questions": true,
    "response_format": "markdown",
    "temperature": 0.0,
    "timezone": "Asia/Taipei",
    "document_field_mapping": {"title": "標題", "content": "內容", "date": "日期", "category": "分類"},
    "selected_index": [],
    "search_mode": "traditional",
    "agent_max_iterations": 5,
    "...其餘欄位...": "...",
    "version_no": 3
  },
  "error": false, "message": "成功獲取配置", "code": 0, "http_status": 200
}
```

儲存值不符現行界線時回 400 `INVALID_CONFIG`，`detail` 指名欄位。

### 更新預設配置
```
PUT http://localhost:5555/api/config/?source=params_edit
```

局部更新，只送要改的欄。`?source=` 是這次寫入的版本來源標記（選填，預設 `edit`，超過 50 字截斷），供版本清單過濾。

⚠️ 不能把 `GET` 回應原樣 PUT 回去：`version_no` 不是設定欄位，寫入模型禁止未知鍵，會 422。要 GET 改 PUT 的話先剝掉 `version_no`。

```
curl -X PUT "http://localhost:5555/api/config/?source=params_edit" \
  -H "Content-Type: application/json" \
  -d '{"role": "AI助手", "model_name": "openai:gpt-5-nano", "search_selected_number": 10, "enable_suggest_questions": false}'
```

回應的 `json_data` 是**你送了什麼**，不是整份設定；要整份請再 GET。

### 列出所有配置（分頁）
```
GET http://localhost:5555/api/config/list?limit=1000&offset=0
```

`limit` 預設 100、上限 1000；`offset` 上限 100000。回**裸清單**（沒有 `total_count`），每一列是完整的設定投影加 `id`、`config_name`、`version_no`、`created_at`、`updated_at`。不是輕量摘要，設定頁清單請自己挑欄位。

### 依名稱讀取、建立、更新、刪除
```
GET    http://localhost:5555/api/config/{config_name}
POST   http://localhost:5555/api/config/{config_name}?source=...
PUT    http://localhost:5555/api/config/{config_name}?source=...
DELETE http://localhost:5555/api/config/{config_name}
```

- `GET`：不存在回 404 `CONFIG_NOT_FOUND`，不會自建。回應同 `GET /api/config/`（含 `version_no`）。
- `POST`：建立。名稱上限 100 字（超過 422）；**保留字不可用**：`default`、`list`、`models`、`rerankers`、`builtin-tools`、`limits`（400 `INVALID_OPERATION`）；已存在回 400。可只送部分欄，其餘落預設。回應是你送的欄位。
- `PUT`：局部更新；不存在回 404，不會建新。
- `DELETE`：`default` 不可刪（400）。**不檢查是否被聊天室引用**：被引用的 config 照刪，既有房間靠自己凍結的值繼續運作，只是 `config_name`＋`config_version` 這個標籤不再可解析。回應 `{"deleted_config": "<name>"}`。

```
curl -X POST http://localhost:5555/api/config/support_bot \
  -H "Content-Type: application/json" \
  -d '{"role": "客服助手", "model_name": "openai:gpt-5-mini", "product_system_prompt": "你是客服助手。", "search_selected_number": 6, "search_total_number": 12, "selected_index": ["faq_docs"]}'
```

### 從另一份配置複製欄位群組
```
POST http://localhost:5555/api/config/{config_name}/copy-from
```

```json
{"source": "default", "groups": ["prompts", "retrieval"]}
```

`groups` 可用值：`prompts`、`retrieval`、`policy`、`tools`、`glossary`、`skills`、`dataset_binding`（完整值域看 `GET /api/config/limits` 的 `config.copy_groups`）。目標不存在時整份建新。版本史記一筆 `source=copy:<來源>`。

### 版本歷史
```
GET  http://localhost:5555/api/config/{config_name}/versions?changed=product_system_prompt&source=prompt_edit
GET  http://localhost:5555/api/config/{config_name}/versions/{version_no}
POST http://localhost:5555/api/config/{config_name}/versions/{version_no}/restore
```

- 列出版本回 `{"versions": [{"version_no": 3, "source": "edit", "changed_fields": ["temperature"], "created_at": "...", "is_current": true}, ...]}`，`version_no` 降序，無分頁。`changed` 只回該欄有變動的版本；`source` 等值過濾。
- 取某一版回當時的整份設定快照（不含 `version_no`）。版本不存在回 404 `CONFIG_VERSION_NOT_FOUND`（2019），與 config 不存在的 2009 分開。
- 還原是 **append 一個新版**（內容＝該舊版，`source=restore_vN`），不是就地改寫。舊版引用的表或 skill 已被刪除時回 400 並說明。

聊天室的 `config_version` 就是這裡的版本號：`GET /api/config/{config_name}/versions/{config_version}` 可取回那間房建立時用的整份設定。

### 目錄端點（設定頁的選單來源）

這幾支回的是機器可讀的目錄，設定頁據此生成選單與輸入框界線，**不要自己維護一份清單**。

| 方法 | 路徑 | 回什麼 |
| --- | --- | --- |
| GET | `/api/config/models` | `{models: [...]}`，每筆 8 個鍵：`value`（寫進 `model_name` 的值）、`label`、`provider`（`openai`／`anthropic`／`vllm`／`ollama`）、`reasoning_options`（思考深度枚舉，空陣列＝沒有這個旋鈕）、`reasoning_default`、`omit_temperature`（true＝該模型拒收 temperature）、`adaptive_thinking`（true＝沒有深度旋鈕但會回推理內容）、`max_output_tokens`（0＝不宣告）。vLLM 那幾筆是執行期實況 |
| GET | `/api/config/rerankers` | 重排序器目錄與各自的分數尺；`qa_direct_score_floor`／`grounding_score_floor` 比的就是這把尺 |
| GET | `/api/config/builtin-tools` | 內建工具全集，每筆 `name`、`description`、`params`、`allowed_in_skill`、`skill_restriction_reason`、`requires` |
| GET | `/api/config/limits` | 寫入層界線目錄 ＋ 逐欄的模式適用性宣告。**界線分組**是 `chat`／`config`／`policy`／`glossary`／`alias`／`skill`／`mcp`／`pagination`，葉節點 `{min?, max?, step?, note?, applies_when?, values?}`，只放存在的那一邊。`chat` 分組含 `chat_room_id`、`chat_log_id`、`prompt_version` 三個請求體識別欄的界線，組請求體的地方也該讀它。⚠️ **另有一顆與那八組平行的頂層鍵 `applies_to`**，葉節點形狀完全不同——見下方 |

```json
{"models": [
  {"value": "openai:gpt-5-mini", "label": "GPT-5 mini", "provider": "openai",
   "reasoning_options": ["minimal", "low", "medium", "high"], "reasoning_default": "low",
   "omit_temperature": false, "adaptive_thinking": false, "max_output_tokens": 0}
]}
```


### `applies_to`：哪一顆設定在哪一種模式下有作用

`GET /api/config/limits` 除了八個界線分組，另有一顆**平行的頂層鍵** `applies_to`。它答的是
另一個問題：**這一顆設定在哪一種問答模式（`search_mode`）下有管道生效**——判準是執行路徑
上有沒有讀取點，只有引擎答得出來。設定頁據此標註適用性，**不要自己維護一份對照表**。

鍵是**欄位名**（config 的每一顆，一顆不缺，另加四顆逐請求欄位 `single_prompt`／`dsl`／
`document_ids`／`intent_context`），與界線分組的鍵（界線名）不同。

```json
{
  "chat": { "human_content_len": {"max": 2000} },
  "applies_to": {
    "index_tiers": {
      "applies_to": "both", "read_in": ["traditional", "agent"],
      "note": "…", "dimensions": {"scope": "both", "tiering": "traditional"}
    },
    "agent_max_iterations": {
      "applies_to": "agent", "read_in": ["agent"], "note": "…"
    }
  }
}
```

葉節點必有 `{applies_to, read_in, note}`，可選 `dimensions`。值域是**五個字面、閉合**：

| 值 | 意思 |
| --- | --- |
| `both` | 兩條答題路徑都有讀取點 |
| `agent` | 只有 `search_mode="agent"` 那條有 |
| `traditional` | 只有另一條有 |
| `none` | 兩條問答路徑都不讀，作用在別處（`note` 會指出在哪） |
| `unknown` | 判不出來（原樣交給第三方、看不到消費點的欄位） |

收到第六種請 fail loud，不要猜、也不要落回預設。

#### ⚠️ 兩個一定要避開的實作

**一、界線那支通用走訪不要走到 `applies_to` 上。** 你如果寫了
`for (const g of Object.values(json)) for (const leaf of Object.values(g)) leaf.max`，
它會掃出七十幾顆 `max === undefined` 的假葉節點，設定頁替每一顆畫一個無上界的輸入框。
先 `if (name === "applies_to") continue`。

**二、`applies_to === "none"` **不等於**可以隱藏。** 它答的是「問答時哪條路徑會讀它」，
不是「設定頁畫不畫」——它是拿來**標註**適用性的，不是可見性開關。config 的每一顆都設得
進去、都在某處有作用。把它接成「`none` ⇒ 隱藏」是最自然的那個實作，而對三顆 `none`
**全部都錯**：

| 欄位 | 為什麼還是要畫 |
| --- | --- |
| `welcome_message` | 開場白由呼叫端自己渲染，不畫就沒地方設 |
| `alias_table_ids` | 它是每一顆 `alias_apply_*` 的取值全集，不畫它那四顆一個都設不了 |
| `ragas_model_name` | 離線評測端點用的 |

同理，`agent`／`traditional` 那幾顆若在不符的模式下**整個藏起來**，值仍然在——租戶看不到
也清不掉，切換模式時那些他從沒見過的值會當場生效。灰掉並附上 `note` 可以同時避開兩邊。

`dimensions` 出現時代表**一個字面值蓋不住這一欄**（`index_tiers` 就是：範圍那一維兩模式
同值、分層行為那一維只有傳統有），設定頁要照它分維度標示，不要壓成單一標示。

`read_in` 是導出 `applies_to` 的依據，四個區域：`traditional`／`agent`／`qa_direct`／
`shared`，後兩者與模式正交（讀到就是 `both`）。

---

## 其他端點總覽

以下端點的完整欄位與界線見 chatbot 服務自己的 README，這裡只列路徑與一句話語意。

### MCP Server 管理（agent 模式的外部工具）
| 方法 | 路徑 | 說明 |
| --- | --- | --- |
| GET | `/api/mcp/servers` | 列出（分頁）。讀取回應的 `env` 值一律遮成 `***`，`args` 裡疑似憑證也遮 |
| POST | `/api/mcp/servers` | 新增。`command` 限白名單 `python`／`python3`／`node`／`npx`／`uvx`／`uv`；`args` 帶明文憑證回 400，憑證請走 `env` |
| GET / PUT / DELETE | `/api/mcp/servers/{id}` | 單台讀、改、刪。PUT 帶回 `***` 代表沿用原值；被 config 或 skill 引用時拒刪（400）。DELETE 回 `{id, name}` |
| GET | `/api/mcp/servers/{id}/tools` | 真的連上去列工具 |
| POST | `/api/mcp/servers/{id}/test` | 連線測試 |

config 的 `mcp_server_ids` 決定哪幾台對該設定生效，空清單＝不連任何 MCP。

### 術語表、別名表、Skill（租戶知識資產）
| 方法 | 路徑 | 說明 |
| --- | --- | --- |
| GET / POST | `/api/glossary?tenant_id=…` | 列表（`tenant_id` 必填）、建表 |
| GET / PUT / DELETE | `/api/glossary/{table_id}` | 單表讀（含詞條，分頁）、改中繼資料、刪表 |
| PUT | `/api/glossary/{table_id}/entries` | 整批取代詞條 |
| GET / POST | `/api/alias?tenant_ref=…` | 列表（`tenant_ref` 必填）、建表 |
| GET / PUT / DELETE | `/api/alias/{table_id}` | 單表讀、改、刪 |
| PUT | `/api/alias/{table_id}/entries` | 整批取代詞條，`version` +1 |
| GET / POST | `/api/skills?tenant_id=…` | 列表、建立 |
| GET / PUT / DELETE | `/api/skills/{skill_id}` | 單筆讀、改（每次寫入存版本）、刪 |
| GET / POST | `/api/skills/{skill_id}/versions[/{version}[/restore]]` | 版本歷史、取版、還原 |

掛到設定的方式：`glossary_table_ids`（授權哪幾張）與 `glossary_apply_agent`（主 agent 看得到哪幾張）、`alias_table_ids` 與四個 `alias_apply_*`、`skill_ids`。

別名表的四個套用階段各自獨立，取值都必須 ⊆ `alias_table_ids`——**設定頁要畫四顆旋鈕**：

| 欄位 | 階段 | 作用 |
| --- | --- | --- |
| `alias_apply_match` | 比對期 | QA 直答完全命中的比對鍵 |
| `alias_apply_search` | 檢索期 | 查詢裡出現的別名，把標準詞**附加**在後面（不取代）。關鍵字檢索那半是 BM25，文件裡沒有字面出現的簡稱就是零命中 |
| `alias_apply_reasoning` | 推理期 | 術語表 lookup 工具（只有 agent 模式存在這顆工具） |
| `alias_apply_output` | 出口期 | 改寫使用者看得到的字 |

四條的別名加總各有各的上限（見 `GET /api/config/limits` 的 `alias` 分組）。撞到時回 400，訊息會指名是哪幾張表各貢獻幾個別名——**把最大的那幾張移出該欄即可，移出一個階段不影響它在其他階段的作用**。被引用的表或 skill 不可刪（400）。not-found 各有自己的碼：2014、2016、2015，版本不存在是 2018。

### QA 直答比對鍵（資料健檢用）

```
POST http://localhost:5555/api/qa-direct/match-keys
```

把 QA 直答「完全命中」那一層的比對鍵投影出來，答的是「**這兩題會不會被判成同一題**」。
用途是對一份 QA 語料做健檢（重複題、同一題掛在兩個主題底下），而判準必須與執行期逐位元組
相同——自己重建一把尺的話，兩個方向的漂移都是靜默的：尺變寬就一筆發現都不產生（讀起來與
「這批資料很乾淨」一模一樣），尺變緊就報出一批根本不會相撞的假重複。

| 欄位 | 必填 | 說明 |
| --- | --- | --- |
| `config_name` | ✅ | 要套用哪份設定的**比對期**別名表。上限 100 字 |
| `questions` | ✅ | 原文送進來，不必先自己正規化。項數上限見 `chat.match_key_questions`，逐項見 `chat.human_content_len` |

`config_name` 沒有預設值是刻意的：比對鍵的最後一段是別名替換（取自該設定的
`alias_apply_match`），一支不吃別名的正規化對綁了表的租戶會**系統性偏窄**。要不套別名的鍵，
就指一份 `alias_apply_match` 為空的設定。

```json
{"config_name": "tenant-a", "questions": ["推廣貿易服務費要如何繳納？", "推貿費要如何繳納", "？？？"]}
```

回應（`json_data`）：

| 欄位 | 說明 |
| --- | --- |
| `qa_direct_active` | 這份設定之下直答**跑不跑得起來**（總開關 ＋ QA 集非空）。見下方 ⚠️ |
| `alias_table_ids` | **實際套用**的別名表（＝該設定的 `alias_apply_match`），不是 `alias_table_ids` 那顆授權全集 |
| `results` | 與 `questions` **同序等長**，靠位置對回去 |

`results[]`：`question`（回聲）、`key`（比對鍵）、`matchable`、`alias_hits`（去重、有上界）、
`alias_hits_truncated`。

**分群規則**：`key` 相同**且** `matchable` 為真 ⇒ 直答會判成同一題。

#### ⚠️ 兩個判準，少一個就會誤報

**一、分群前先濾掉 `matchable === false`。** 正規化後為空的標題一律不命中，所以純空白、
純標點（`？？？`）那一類的 `key` 都是 `""`，而**它們彼此不會相撞**。只看 `key` 的話，
那一整類會被算成一個巨大的重複群——而它正是匯入資料最常見的一類（空儲存格、佔位列）。

**二、`qa_direct_active === false` 的話，這一批不值得分群。** 比對鍵回答的是「使用者會不會
拿到兩個答案」，而那個問題在直答根本不會發生的設定上沒有意義。不可能相撞的狀態有三種，
而**只有一種是 404**：

| 狀態 | 回應 |
| --- | --- |
| 設定不存在 | **404 `code=2009`** |
| `qa_direct_enabled=false` | 200，`qa_direct_active: false` |
| `qa_direct_indexes=[]` | 200，`qa_direct_active: false` |

只靠 404 分流的話，後兩種會被當成正常結果照常分群。鍵仍然照算——這一顆答的是「值不值得
分群」，不是「算不算得出鍵」。

`code=2009` **只在「這份設定不存在」時出現**（版本不存在是另一顆碼 2019），可以據它分流。
一個從來沒問過任何一題的租戶還沒有設定，那一次健檢請標成「未評估」，不要當成乾淨。

### RAGAS 批次評測
| 方法 | 路徑 | 說明 |
| --- | --- | --- |
| POST | `/api/chat/ragas_batch_evaluation` | 對既有紀錄跑指標。`chat_log_ids` 最多 100 筆；`metrics` 可選 `faithfulness`／`response_relevancy`／`context_precision`；同時只跑一批。不佔每日配額 |
| GET | `/api/chat/ragas_batch_evaluation/{task_id}` | 狀態與逐筆結果（`include_results`、`limit`、`offset`）。任務不落庫，重啟後查不到，回 404 `RAGAS_TASK_NOT_FOUND`（2017） |
| DELETE | `/api/chat/ragas_batch_evaluation/{task_id}` | 取消 |

請求端的 `response_relevancy` 在結果裡叫 `answer_relevancy`，要自己對映。

### 健康檢查
```
GET http://localhost:5555/health
GET http://localhost:5555/health/database
```

不健康時回 **503**，body 仍是同一個信封。

---

## 錯誤處理

### 錯誤信封
```json
{"json_data": "", "error": true, "message": "無效的輸入: human_content: 1–2000 字（下限為 strip() 後非空）", "code": 1001, "http_status": 400}
```

- `message` 的基底句隨 `Accept-Language`，`detail` 以 `: ` 接在後面（422 與 429 的 `detail` 必定存在）。
- 指得出是哪一個輸入欄出錯的拒絕會多帶 `field`（目前只有 MCP 寫入面的 `env`／`args`）。不帶時鍵集不變。
- 串流一旦開始就回不到這個信封，中途失敗只能以 `chunk_type: "error"` 收尾。所以輸入不合法的情況一律在開串流之前擋。

### 錯誤代碼表

HTTP 狀態碼不是由碼段推出來的。not-found 家族 404、請求體不符 schema 422、額度超限 429、上游額度服務不可用 503，其餘 1xxx 是 400、2xxx 是 500。

| Code | 名稱 | HTTP | 說明 |
| --- | --- | --- | --- |
| 0 | SUCCESS | 200 | 成功 |
| 1000 | GENERAL_ERROR | 400 | 保留碼，沒有端點會送出；不要拿它當兜底 |
| 1001 | INVALID_INPUT | 400 | 值不合法（長度、分頁、時間格式、DSL 不平衡） |
| 1004 | INVALID_CONFIG | 400 | 設定內容不合法（寫入驗證失敗；儲存值或凍結快照不符現行界線） |
| 1005 | INVALID_OPERATION | 400 | 不合法的操作（保留字、已存在、刪 default、被引用不能刪、框架 404／405） |
| 2000 | DATABASE_ERROR | 500 | 資料庫錯誤 |
| 2002 | CHAT_ROOM_NOT_FOUND | 404 | 聊天室不存在或已刪除 |
| 2003 | CHAT_LOG_NOT_FOUND | 404 | 聊天記錄不存在或已刪除 |
| 2008 | CONFIG_ERROR | 500 | 設定處理錯誤 |
| 2009 | CONFIG_NOT_FOUND | 404 | 設定不存在 |
| 2010 | MCP_CONNECTION_ERROR | 500 | MCP Server 連線或啟動失敗（子行程名額滿時同碼改回 503） |
| 2012 | MCP_COMMAND_NOT_FOUND | 404 | MCP 指令在白名單內但執行檔不在 PATH |
| 2013 | MCP_SERVER_NOT_FOUND | 404 | MCP Server 不存在 |
| 2014 | GLOSSARY_TABLE_NOT_FOUND | 404 | 術語表不存在 |
| 2015 | SKILL_NOT_FOUND | 404 | skill 不存在 |
| 2016 | ALIAS_TABLE_NOT_FOUND | 404 | 別名表不存在 |
| 2017 | RAGAS_TASK_NOT_FOUND | 404 | 評測任務不存在 |
| 2018 | SKILL_VERSION_NOT_FOUND | 404 | skill 存在但該版本不存在 |
| 2019 | CONFIG_VERSION_NOT_FOUND | 404 | 設定存在但該版本不存在 |
| 2020 | USAGE_LIMIT_UNAVAILABLE | 503 | 上游額度服務不可用 |
| 2999 | INTERNAL_SERVER_ERROR | 500 | 未預期的伺服器例外（真正的兜底） |
| 3001 | INVALID_REQUEST_BODY | 422 | 請求體或 query 參數不符 schema |
| 4003 | DAILY_USAGE_LIMIT_EXCEEDED | 429 | 已達每日使用上限 |

### 三個容易誤判的區分

- **422 vs 400**：422 是「這個請求我連解析都解析不出來」（型別錯、缺必填、多了未知欄、超過欄位上界），要改形狀；400 是「形狀對、業務上不合法」，要改值。
- **429 vs 403**：每日額度用完是「今天用完、明天可以」，可重試；重試策略與告警分級靠這個區分。
- **404 vs 500**：找不到資源是呼叫端給錯 id，不是服務故障；記成 500 會讓告警噪音蓋掉真正的故障。

### `INVALID_CONFIG` 也會從讀取路徑回來

config 的儲存值今天驗不過現行界線時，`GET /api/config/`、`GET /api/config/{name}`、`POST /api/chat/chatbot`、`POST /api/chat/ragas_batch_evaluation` 都回 400 `INVALID_CONFIG`，`detail` 指名欄位。症狀是「這份 config 的新對話一律失敗、既有房間走凍結值照樣續談」。補救是重新寫入那幾欄，或還原到一個合法的歷史版本。反過來，房間凍結快照驗不過時，`detail` 會指名 `chat_room_id=… 的凍結快照`，症狀是「這間房續談不了、開新對話正常」。

### 錯誤回應範例

```json
{"json_data": "", "error": true, "message": "找不到聊天室: ID: 123", "code": 2002, "http_status": 404}
```
```json
{"json_data": "", "error": true, "message": "請求內容格式不正確: human_content: Field required", "code": 3001, "http_status": 422}
```
```json
{"json_data": "", "error": true, "message": "請求內容格式不正確: 這支端點沒有這些查詢參數：'timezone'", "code": 3001, "http_status": 422}
```
```json
{"json_data": "", "error": true, "message": "已達每日使用上限: 5/5", "code": 4003, "http_status": 429}
```
```json
{"json_data": "", "error": true, "message": "這個配置名稱是保留字（'default'，以及設定 API 的靜態路徑 list／models／rerankers／builtin-tools／limits），請換一個名字: 'limits'", "code": 1005, "http_status": 400}
```
