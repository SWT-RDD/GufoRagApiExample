# GufoRAG Chat API 使用說明文件

## API 概述
本 API 提供聊天對話與聊天室管理功能，支援 Server-Sent Events (SSE) 串流回應。所有 Chat API 端點均位於 `/api/chat` 路徑下。

## 聊天對話 API (SSE 串流)

### URL
POST http://localhost:8000/api/chat/chatbot

### 請求格式
| KEY            | VALUE                |
| -------------- | -------------------- |
| Content-Type   | application/json     |

### 請求資料範例
```json
{
  "chat_room_id": null,
  "chat_log_id": null,
  "human_content": "請問什麼是人工智慧？",
  "config_name": "default"
}
```

#### 請求參數說明
| KEY                   | VALUE                       |
| --------------------- | --------------------------- |
| chat_room_id          | 聊天室ID，null表示新建聊天室    |
| chat_log_id           | 聊天記錄ID，null表示新建記錄   |
| human_content         | 使用者輸入內容，3-2000字      |
| config_name           | 配置名稱，預設為 "default"    |

### curl 請求範例
```
curl -X POST http://localhost:8000/api/chat/chatbot \
  -H "Content-Type: application/json" \
  -d '{
    "chat_room_id": null,
    "chat_log_id": null,
    "human_content": "請問什麼是人工智慧？",
    "config_name": "default"
  }'
```

### 回應資料格式 (SSE 串流)
回應採用 Server-Sent Events 格式，每行以 `data: ` 開頭：

#### 訊息塊 (chunk_type: "message")
```
data: {"chunk_type": "message", "content": "人工智慧是", "data": {"content": "人工智慧是"}}
```

#### 聊天室資訊塊 (chunk_type: "chat_room")
```
data: {
  "chunk_type": "chat_room", 
  "content": "", 
  "data": {
    "id": 123,
    "title": "AI助手對話",
    "description": "一般AI對話",
    "role": "智能助手",
    "model_name": "openai:gpt-4o",
    "status": "active",
    "chat_logs_count": 1,
    "latest_chat_log_id": 456,
    "suggest_questions": ["相關問題1", "相關問題2"],
    "search_results": [],
    "created_at": "2024-01-01T10:00:00Z",
    "updated_at": "2024-01-01T12:00:00Z"
  }
}
```

#### 結束塊 (chunk_type: "end")
```
data: {"chunk_type": "end", "content": "", "data": {"status": "completed"}}
```

## 聊天室列表 API

### URL
GET http://localhost:8000/api/chat/chatrooms

### 請求格式
無需特殊標頭，直接發送 GET 請求即可。

### curl 請求範例
```
curl http://localhost:8000/api/chat/chatrooms
```

### 回應資料範例
```json
{
  "json_data": [
    {
      "id": 1,
      "title": "AI助手對話",
      "description": "一般AI對話",
      "role": "智能助手",
      "chatroom_system_prompt": "你是一個專業的AI助手...",
      "product_system_prompt": "產品相關提示詞...",
      "intension_system_prompt": "意圖分析提示詞...",
      "status": "active",
      "model_name": "openai:gpt-4o",
      "active_chain_end_id": null,
      "search_selected_number": 5,
      "search_total_number": 10,
      "data_source_ratio": 0.7,
      "use_knowledge_mode": "strict",
      "enable_rerank": true,
      "memory_count": 3,
      "response_format": "markdown",
      "enable_suggest_questions": true,
      "temperature": 0.7,
      "document_field_mapping": {},
      "chat_logs_count": 5,
      "created_at": "2024-01-01T10:00:00Z",
      "updated_at": "2024-01-01T12:00:00Z"
    }
  ],
  "error": false,
  "message": "成功取得聊天室列表",
  "code": 0,
  "http_status": 200
}
```

#### 聊天室資料欄位說明
| KEY                      | VALUE                     |
| ------------------------ | ------------------------- |
| id                       | 聊天室ID                  |
| title                    | 聊天室標題                |
| description              | 聊天室描述                |
| role                     | 機器人身份設定            |
| chatroom_system_prompt   | 聊天室系統提示詞          |
| product_system_prompt    | 產品系統提示詞            |
| intension_system_prompt  | 意圖系統提示詞            |
| status                   | 狀態 (active/inactive)    |
| model_name               | 使用的AI模型名稱          |
| active_chain_end_id      | 活躍對話鏈結尾ID          |
| search_selected_number   | 使用的搜索結果數量        |
| search_total_number      | 檢索的總結果數量          |
| data_source_ratio        | 詞與向量搜索的比例        |
| use_knowledge_mode       | 知識使用模式              |
| enable_rerank            | 是否重新排序結果          |
| memory_count             | 記住的歷史消息數量        |
| response_format          | 回應格式                  |
| enable_suggest_questions | 是否啟用系統推薦問題      |
| temperature              | AI回應的創造性/隨機性     |
| document_field_mapping   | 文件欄位映射關係          |
| chat_logs_count          | 聊天記錄數量              |
| created_at               | 建立時間                  |
| updated_at               | 更新時間                  |

## 聊天記錄查詢 API

### URL
GET http://localhost:8000/api/chat/chatrooms/{chat_room_id}/chatlogs

### 請求參數
- `chat_room_id`: 聊天室ID (路徑參數)

### curl 請求範例
```
curl http://localhost:8000/api/chat/chatrooms/1/chatlogs
```

### 回應資料範例
```json
{
  "json_data": [
    {
      "id": 1,
      "chat_room_id": 1,
      "previous_chat_log_id": null,
      "human_content": "你好",
      "ai_content": "你好！有什麼我可以幫助你的嗎？",
      "human_time": "2024-01-01T10:00:00+08:00",
      "ai_time": "2024-01-01T10:00:05+08:00",
      "suggest_questions": ["相關問題1", "相關問題2"],
      "search_results": [],
      "language": "繁體中文",
      "is_coding": false,
      "query_start_time": null,
      "query_end_time": null,
      "keywords": ["問候", "聊天"],
      "question": "使用者想要問候並開始對話"
    }
  ],
  "error": false,
  "message": "成功",
  "code": 0,
  "http_status": 200
}
```

## 聊天記錄評價 API

### 提交評價
POST http://localhost:8000/api/chat/chat_logs/{chat_log_id}/rating

#### 請求資料範例
```json
{
  "rating_type": "positive",
  "feedback": "回答很有幫助"
}
```

#### 評價類型說明
| rating_type | 說明 |
| ----------- | ---- |
| positive    | 好評 |
| negative    | 壞評 |

### 查詢評價
GET http://localhost:8000/api/chat/chat_logs/{chat_log_id}/rating

#### 回應資料範例
```json
{
  "json_data": {
    "chat_log_id": 1,
    "rating_type": "positive",
    "rating_feedback": "回答很有幫助",
    "rating_time": "2024-01-01T10:05:00Z",
    "has_rating": true
  },
  "error": false,
  "message": "獲取評價成功",
  "code": 0,
  "http_status": 200
}
```

## 錯誤處理

### 錯誤回應格式
```json
{
  "json_data": "",
  "error": true,
  "message": "錯誤訊息",
  "code": 1001,
  "http_status": 400
}
```

### Chat API 錯誤代碼
| Code | ErrorCode Name        | HTTP Status | 說明                    |
| ---- | --------------------- | ----------- | ----------------------- |
| 1001 | INVALID_INPUT         | 400         | 輸入驗證錯誤 (如字數限制) |
| 1000 | GENERAL_ERROR         | 400         | 一般處理錯誤            |
| 2000 | DATABASE_ERROR        | 500         | 資料庫連接或查詢錯誤    |
| 2002 | CHAT_ROOM_NOT_FOUND   | 500         | 指定的聊天室ID不存在    |
| 2003 | CHAT_LOG_NOT_FOUND    | 500         | 指定的聊天記錄ID不存在  |