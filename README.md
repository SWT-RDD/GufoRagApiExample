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
  "config_name": "default",
  "user_id": "user123",
  "selected_index": ["technical_docs", "faq_docs"]
}
```

#### 請求參數說明
| KEY                   | VALUE                       |
| --------------------- | --------------------------- |
| chat_room_id          | 聊天室ID，null表示新建聊天室    |
| chat_log_id           | 聊天記錄ID，null表示新建記錄   |
| human_content         | 使用者輸入內容，3-2000字      |
| config_name           | 配置名稱，預設為 "default"    |
| user_id               | 使用者ID，選填，用於識別用戶身份 |
| selected_index        | 選擇的文件索引列表，陣列型態(如["technical_docs", "faq_docs"])，可用於指定本次查詢要檢索的索引，若未傳則使用config預設值 |

### curl 請求範例
```
curl -X POST http://localhost:8000/api/chat/chatbot \
  -H "Content-Type: application/json" \
  -d '{
    "chat_room_id": null,
    "chat_log_id": null,
    "human_content": "請問什麼是人工智慧？",
    "config_name": "default",
    "user_id": "user123",
    "selected_index": ["technical_docs", "faq_docs"]
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
        "document": {
          "title": "人工智慧基礎概念",
          "content": "人工智慧（Artificial Intelligence, AI）是一門跨領域的科學...",
          "date": "2024-01-01",
          "category": "技術文件",
          "search": "人工智慧基礎概念 人工智慧（Artificial Intelligence, AI）是一門跨領域的科學..."
        }
      }
    ],
    "created_at": "2024-01-01T10:00:00Z",
    "updated_at": "2024-01-01T12:00:00Z"
  }
}
```

#### 結束塊 (chunk_type: "end")
```
data: {"chunk_type": "end", "content": "", "data": {"status": "completed"}}
```

### 檢索結果格式說明 (search_results)

`search_results` 是一個文件檢索結果的陣列，每個搜索結果包含以下欄位：

#### DocumentResult 格式表
| 欄位名稱      | 類型     | 說明                                     |
| ------------- | -------- | ---------------------------------------- |
| doc_name      | string   | 文件名稱                                 |
| document_id   | string   | 文件的唯一識別碼                         |
| chunk_index   | integer  | 該文件的分塊索引 (從0開始)               |
| data_source   | string   | 文件資料來源                             |
| index         | string   | 文件所屬的索引                           |
| search_mode   | string   | 搜尋策略 (weaviate_only/gufonet_only/hybrid) |
| last_modified | string   | 最後修改時間 (ISO格式)                   |
| score         | float    | 搜尋相關性分數                           |
| document      | object   | 文件內容字典，包含實際的文件資料         |

#### search_results 範例
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

#### document 物件內容說明
`document` 物件包含文件的實際內容，常見欄位包括(這要對應你匯入時放在content的欄位，search是search欄位)：

| 欄位名稱 | 類型   | 說明                       |
| -------- | ------ | -------------------------- |
| title    | string | 文件標題                   |
| content  | string | 文件內容文字               |
| date     | string | 文件日期                   |
| category | string | 文件分類                   |
| author   | string | 文件作者                   |
| summary  | string | 文件摘要                   |
| search   | string | 觸發此結果的搜尋查詢文字。大概都會有。   |

**注意**: `document` 物件的具體欄位會根據文件類型和索引配置而有所不同。

## 聊天室列表 API

### 獲取所有聊天室
#### URL
GET http://localhost:8000/api/chat/chatrooms

#### 請求格式
無需特殊標頭，直接發送 GET 請求即可。

#### curl 請求範例
```
curl http://localhost:8000/api/chat/chatrooms
```

### 根據使用者ID獲取聊天室
#### URL
GET http://localhost:8000/api/chat/chatrooms/user/{user_id}

#### 請求參數
- `user_id`: 使用者ID (路徑參數)
- `limit`: 限制返回的聊天室數量 (查詢參數，可選)

#### curl 請求範例
```
curl http://localhost:8000/api/chat/chatrooms/user/user123
curl http://localhost:8000/api/chat/chatrooms/user/user123?limit=10
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
      "is_coding": false,
      "query_start_time": "2024-01-01T10:00:01+08:00",
      "query_end_time": "2024-01-01T10:00:03+08:00",
      "keywords": ["人工智慧", "AI", "技術"],
      "question": "使用者詢問人工智慧的定義和概念"
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


### 配置參數說明 (以下api請參考此參數)
| 參數名稱                    | 類型    | 預設值                      | 說明                                      |
| -------------------------- | ------- | --------------------------- | ----------------------------------------- |
| product_system_prompt      | string  | "你是一個智能助手..."        | 產品系統提示詞                            |
| role                       | string  | "智能助手"                   | 機器人身份設定                            |
| model_name                 | string  | "openai:gpt-4o"             | 使用的AI模型名稱                          |
| search_selected_number     | integer | 8                           | 實際用於上下文的搜索結果數量               |
| search_total_number        | integer | 16                          | 檢索的總搜索結果數量                       |
| data_source_ratio          | float   | 0.0                         | 詞向量搜索比例 (0.0=純向量, 1.0=純關鍵字) |
| use_knowledge_mode         | string  | "strict"                    | 知識使用模式：none/assist/strict          |
| enable_rerank              | boolean | true                        | 是否重新排序搜索結果                       |
| memory_count               | integer | 5                           | 記住的歷史對話數量                         |
| enable_suggest_questions   | boolean | true                        | 是否啟用推薦問題                          |
| response_format            | string  | "markdown"                  | 回應格式：markdown/html                   |
| document_field_mapping     | object  | {"title":"標題",...}        | 文件欄位映射表                            |
| selected_index             | array   | []                          | 選擇的文件索引列表                         |


## 配置管理 API

### 獲取預設配置
#### URL
GET http://localhost:8000/api/config/

#### curl 請求範例
```
curl http://localhost:8000/api/config/
```

#### 回應資料範例
```json
{
  "json_data": {
    "chatroom_system_prompt": "",
    "product_system_prompt": "你是一個智能助手，專門回答用戶的問題。",
    "intension_system_prompt": "",
    "role": "智能助手",
    "model_name": "openai:gpt-4o",
    "search_selected_number": 8,
    "search_total_number": 16,
    "data_source_ratio": 0.0,
    "use_knowledge_mode": "strict",
    "enable_rerank": true,
    "memory_count": 5,
    "enable_suggest_questions": true,
    "response_format": "markdown",
    "temperature": 0.0,
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
  "message": "配置獲取成功",
  "code": 0,
  "http_status": 200
}
```


### 更新預設配置
#### URL
PUT http://localhost:8000/api/config/

#### 請求資料範例
```json
{
  "role": "AI助手",
  "model_name": "openai:gpt-4o-mini",
  "search_selected_number": 10,
  "enable_suggest_questions": false
}
```

#### curl 請求範例
```
curl -X PUT http://localhost:8000/api/config/ \
  -H "Content-Type: application/json" \
  -d '{
    "role": "AI助手",
    "model_name": "openai:gpt-4o-mini",
    "search_selected_number": 10,
    "enable_suggest_questions": false
  }'
```

### 獲取所有配置列表
#### URL
GET http://localhost:8000/api/config/list

#### curl 請求範例
```
curl http://localhost:8000/api/config/list
```

#### 回應資料範例
```json
{
  "json_data": [
    {
      "id": 1,
      "config_name": "default",
      "role": "智能助手",
      "model_name": "openai:gpt-4o",
      "created_at": "2024-01-01T10:00:00Z",
      "updated_at": "2024-01-01T12:00:00Z"
    },
    {
      "id": 2,
      "config_name": "stupid_assistant",
      "role": "不智能助手",
      "model_name": "openai:gpt-4o",
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

### 根據名稱獲取特定配置
#### URL
GET http://localhost:8000/api/config/{config_name}

#### 請求參數
- `config_name`: 配置名稱 (路徑參數)

#### curl 請求範例
```
curl http://localhost:8000/api/config/stupid_assistant
```

### 創建新配置
#### URL
POST http://localhost:8000/api/config/{config_name}

#### 請求參數
- `config_name`: 配置名稱 (路徑參數，不可使用 "default")

#### 請求資料範例
```json
{
  "role": "不智能助手",
  "model_name": "openai:gpt-4o",
  "product_system_prompt": "你是一個不智能助手，專門亂回問題。",
  "search_selected_number": 6,
  "search_total_number": 12,
  "enable_suggest_questions": false,
  "use_knowledge_mode": "strict"
}
```

#### curl 請求範例
```
curl -X POST http://localhost:8000/api/config/stupid_assistant \
  -H "Content-Type: application/json" \
  -d '{
    "role": "不智能助手",
    "model_name": "openai:gpt-4o",
    "product_system_prompt": "你是一個不智能助手，專門亂回問題。",
    "search_selected_number": 6,
    "search_total_number": 12,
    "enable_suggest_questions": false,
    "use_knowledge_mode": "strict"
  }'
```

### 更新特定配置
#### URL
PUT http://localhost:8000/api/config/{config_name}

#### 請求參數
- `config_name`: 配置名稱 (路徑參數)

#### 請求資料範例
```json
{
  "model_name": "openai:gpt-4o-mini",
  "search_selected_number": 8
}
```

#### curl 請求範例
```
curl -X PUT http://localhost:8000/api/config/stupid_assistant \
  -H "Content-Type: application/json" \
  -d '{
    "model_name": "openai:gpt-4o-mini",
    "search_selected_number": 8
  }'
```

### 刪除配置
#### URL
DELETE http://localhost:8000/api/config/{config_name}

#### 請求參數
- `config_name`: 配置名稱 (路徑參數，不能刪除 "default")

#### curl 請求範例
```
curl -X DELETE http://localhost:8000/api/config/stupid_assistant
```

#### 回應資料範例
```json
{
  "json_data": {
    "deleted_config": "stupid_assistant"
  },
  "error": false,
  "message": "配置 'stupid_assistant' 已刪除",
  "code": 0,
  "http_status": 200
}
```

### 錯誤代碼表 (回到前端時建議500系列都寫系統錯誤就好)
| Code | ErrorCode Name        | HTTP Status | 說明              | 訊息內容                |
| ---- | --------------------- | ----------- | ----------------- | ----------------------- |
| 0    | SUCCESS              | 200         | 成功              | 操作成功                |
| 1000 | GENERAL_ERROR        | 400         | 一般處理錯誤      | 發生一般錯誤            |
| 1001 | INVALID_INPUT        | 400         | 輸入驗證錯誤      | 無效的輸入              |
| 1005 | INVALID_OPERATION    | 400         | 無效操作          | 無效操作                |
| 2000 | DATABASE_ERROR       | 500         | 資料庫操作錯誤    | 資料庫操作錯誤          |
| 2002 | CHAT_ROOM_NOT_FOUND  | 500         | 聊天室不存在      | 找不到聊天室            |
| 2003 | CHAT_LOG_NOT_FOUND   | 500         | 聊天記錄不存在    | 找不到聊天記錄          |
| 2008 | CONFIG_ERROR         | 500         | 配置處理錯誤      | 配置處理錯誤            |
| 2009 | CONFIG_NOT_FOUND     | 500         | 配置不存在        | 找不到配置              |

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
  "message": "配置名稱不可以使用 'default'，這是系統預設配置",
  "code": 1005,
  "http_status": 400
}
```

#### 聊天室不存在錯誤 (500)
```json
{
  "json_data": "",
  "error": true,
  "message": "找不到聊天室",
  "code": 2002,
  "http_status": 500
}
```

#### 配置不存在錯誤 (500)
```json
{
  "json_data": "",
  "error": true,
  "message": "找不到配置",
  "code": 2009,
  "http_status": 500
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