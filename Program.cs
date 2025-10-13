using Newtonsoft.Json;
using System.Text;

// 共用HttpClient
var handler = new HttpClientHandler();
handler.ServerCertificateCustomValidationCallback =
    (httpRequestMessage, cert, cetChain, policyErrors) => true;
HttpClient client = new HttpClient(handler); // 如果碰到SSL憑證問題，可能可以嘗試加上或拿掉handler

string baseUrl = "http://localhost:8000";
string configName = "demo_config"; // 新增用的 config 名稱

Console.WriteLine("=== GufoRAG API 全流程範例 ===\n");

try
{
    // 示範config相關流程(設定主題)
    // 1. 新增 config
    var newConfig = new ConfigRequest
    {
        // 系統提示詞
        ProductSystemPrompt = "你是測試用助手。", 
        // 機器人身份設定
        Role = "測試助手", 
        // 使用的AI模型名稱
        ModelName = "openai:gpt-4o", 
        // 實際用於上下文的搜索結果數量
        SearchSelectedNumber = 3, 
        // 檢索的總搜索結果數量
        SearchTotalNumber = 6, 
        // 詞向量搜索比例 (0.0=純向量, 1.0=純關鍵字)
        DataSourceRatio = 0.0f,
        // 知識使用模式：none/assist/strict (使用模型自有知識/參考資料+模型自有知識/限用參考資料)
        UseKnowledgeMode = "strict", 
        // 是否重新排序搜索結果
        EnableRerank = true, 
        // 記住的歷史對話數量
        MemoryCount = 5, 
        // 是否啟用推薦問題
        EnableSuggestQuestions = true, 
        // 回應格式：markdown/html
        ResponseFormat = "markdown", 
        // 文件欄位映射表 (key: 索引欄位, value: 給AI看的欄位名稱)
        DocumentFieldMapping = new Dictionary<string, string> 
        {
            { "title", "標題" },
            { "search", "內容" } 
        },
        // 選擇的文件索引列表 (資料集)
        SelectedIndex = new List<string> { "貓問答", "狗問答" }
    };
    var createConfigRes = await PostJson($"{baseUrl}/api/config/{configName}", newConfig);
    Console.WriteLine($"[新增 config] {createConfigRes}\n");
    try
    {
        var createConfigObj = JsonConvert.DeserializeObject<JsonResponse<ChatConfig>>(createConfigRes);
        if (createConfigObj?.JsonData != null)
        {
            ChatConfig.Print(createConfigObj.JsonData);
        }
        else
        {
            Console.WriteLine("[警告] 回傳內容無法轉為 ChatConfig");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[反序列化失敗] {ex.Message}");
    }
    await Task.Delay(5000); // 暫停5秒鐘

    // 2. 查詢 config 列表
    var configListRes = await Get($"{baseUrl}/api/config/list");
    Console.WriteLine($"[查詢 config 列表] {configListRes}\n");
    try
    {
        var configListObj = JsonConvert.DeserializeObject<JsonResponse<List<ChatConfig>>>(configListRes);
        if (configListObj?.JsonData != null && configListObj.JsonData.Count > 0)
        {
            foreach (var config in configListObj.JsonData)
            {
                ChatConfig.Print(config);
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("[警告] 查無 config 或格式不符");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[反序列化失敗] {ex.Message}");
    }
    await Task.Delay(5000); // 暫停5秒鐘

    // 3. 刪除 config
    var deleteConfigRes = await Delete($"{baseUrl}/api/config/{configName}");
    Console.WriteLine($"[刪除 config] {deleteConfigRes}\n");
    try
    {
        var deleteConfigObj = JsonConvert.DeserializeObject<JsonResponse<object>>(deleteConfigRes);
        if (deleteConfigObj?.JsonData != null)
        {
            var deletedConfigName = (deleteConfigObj.JsonData as Newtonsoft.Json.Linq.JObject)?["deleted_config"]?.ToString();
            if (!string.IsNullOrEmpty(deletedConfigName))
            {
                Console.WriteLine($"已刪除 config: {deletedConfigName}");
            }
            else
            {
                Console.WriteLine("[警告] 刪除回傳內容無法取得 deleted_config");
            }
        }
        else
        {
            Console.WriteLine("[警告] 刪除回傳內容無法取得 deleted_config");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[反序列化失敗] {ex.Message}");
    }
    await Task.Delay(5000); // 暫停5秒鐘

    // 4. 問答 (SSE串流)
    var chatRequest = new ChatRequest
    {
        ChatRoomId = null,
        ChatLogId = null,
        HumanContent = "請問什麼是人工智慧？",
        ConfigName = "default",
        UserId = "test_user"
    };
    (var chatRoomId, var latestChatLogId) = await ChatWithBot(chatRequest);
    Console.WriteLine();
    await Task.Delay(5000); // 暫停5秒鐘

    // 5. 查詢聊天室列表
    var chatRooms = await GetChatRooms();
    Console.WriteLine();
    await Task.Delay(5000); // 暫停5秒鐘

    // 6. 查詢聊天紀錄 (用剛問完的聊天室ID)
    if (chatRoomId.HasValue)
    {
        var chatLogs = await GetChatLogs(chatRoomId.Value);
        Console.WriteLine();
        await Task.Delay(5000); // 暫停5秒鐘

        // 7. 上傳評論 (用聊天室的 latest_chat_log_id)
        if (latestChatLogId.HasValue)
        {
            await RateChatLog(latestChatLogId.Value);
            Console.WriteLine();
            await Task.Delay(5000); // 暫停5秒鐘
        }

        // 8. 檢查評論是否成功 (再查一次聊天紀錄)
        await GetChatLogs(chatRoomId.Value);
        Console.WriteLine();
        await Task.Delay(5000); // 暫停5秒鐘
    }
}
catch (Exception ex)
{
    Console.WriteLine($"錯誤: {ex.Message}");
}

// --- API 輔助方法 ---

async Task<string> Get(string url)
{
    client.DefaultRequestHeaders.Clear();
    var res = await client.GetAsync(url);
    return await res.Content.ReadAsStringAsync();
}

async Task<string> PostJson(string url, object data)
{
    client.DefaultRequestHeaders.Clear();
    var json = JsonConvert.SerializeObject(data);
    var content = new StringContent(json, Encoding.UTF8, "application/json");
    var res = await client.PostAsync(url, content);
    return await res.Content.ReadAsStringAsync();
}

async Task<string> Delete(string url)
{
    client.DefaultRequestHeaders.Clear();
    var res = await client.DeleteAsync(url);
    return await res.Content.ReadAsStringAsync();
}

// 聊天對話API (SSE串流)
async Task<(int? chatRoomId, int? latestChatLogId)> ChatWithBot(ChatRequest chatRequest)
{
    Console.WriteLine("[問答 (SSE串流)]");
    Console.WriteLine($"使用者輸入: {chatRequest.HumanContent}");
    Console.WriteLine("AI回應:");
    var url = $"{baseUrl}/api/chat/chatbot";
    var jsonContent = JsonConvert.SerializeObject(chatRequest);
    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
    client.DefaultRequestHeaders.Clear();
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.IsSuccessStatusCode)
        {
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            var fullMessage = new StringBuilder();
            string? line;
            int? returnedChatRoomId = null;
            int? latestChatLogId = null;
            while (!reader.EndOfStream)
            {
                line = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(line) && line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    try
                    {
                        var chunk = JsonConvert.DeserializeObject<StreamChunk>(data);
                        switch (chunk?.ChunkType)
                        {
                            case "message":
                                Console.Write(chunk.Content);
                                Console.Out.Flush();
                                fullMessage.Append(chunk.Content);
                                break;
                            case "chat_room":
                                if (chunk.Data != null)
                                {
                                    ChatRoom chatRoom = JsonConvert.DeserializeObject<ChatRoom>(chunk.Data.ToString());
                                    if (chatRoom != null)
                                    {
                                        returnedChatRoomId = chatRoom.Id;
                                        latestChatLogId = chatRoom.LatestChatLogId;
                                        Console.WriteLine("\n[聊天室資訊]");
                                        Console.WriteLine($"  聊天室ID: {chatRoom.Id}");
                                        Console.WriteLine($"  聊天室標題: {chatRoom.Title}");
                                        Console.WriteLine($"  聊天室描述: {chatRoom.Description}");
                                        Console.WriteLine($"  機器人身份設定: {chatRoom.Role}");
                                        Console.WriteLine($"  產品系統提示詞: {chatRoom.ProductSystemPrompt}");
                                        Console.WriteLine($"  狀態: {chatRoom.Status}");
                                        Console.WriteLine($"  使用的AI模型名稱: {chatRoom.ModelName}");
                                        Console.WriteLine($"  使用的搜索結果數量: {chatRoom.SearchSelectedNumber}");
                                        Console.WriteLine($"  檢索的總結果數量: {chatRoom.SearchTotalNumber}");
                                        Console.WriteLine($"  詞與向量搜索的比例: {chatRoom.DataSourceRatio}");
                                        Console.WriteLine($"  知識使用模式: {chatRoom.UseKnowledgeMode}");
                                        Console.WriteLine($"  是否重新排序結果: {chatRoom.EnableRerank}");
                                        Console.WriteLine($"  記住的歷史消息數量: {chatRoom.MemoryCount}");
                                        Console.WriteLine($"  回應格式: {chatRoom.ResponseFormat}");
                                        Console.WriteLine($"  是否啟用系統推薦問題: {chatRoom.EnableSuggestQuestions}");
                                        Console.WriteLine($"  AI回應的創造性/隨機性: {chatRoom.Temperature}");
                                        Console.WriteLine($"  文件欄位映射關係: {(chatRoom.DocumentFieldMapping != null && chatRoom.DocumentFieldMapping.Count > 0 ? string.Join(", ", chatRoom.DocumentFieldMapping.Select(kv => $"{kv.Key}:{kv.Value}")) : "無")}");
                                        Console.WriteLine($"  聊天記錄數量: {chatRoom.ChatLogsCount}");
                                        Console.WriteLine($"  建立時間: {chatRoom.CreatedAt}");
                                        Console.WriteLine($"  更新時間: {chatRoom.UpdatedAt}");
                                        Console.WriteLine($"  選擇的文件索引列表: {(chatRoom.SelectedIndex != null && chatRoom.SelectedIndex.Count > 0 ? string.Join(", ", chatRoom.SelectedIndex) : "無")}");
                                        Console.WriteLine($"  建議問題: {(chatRoom.SuggestQuestions != null && chatRoom.SuggestQuestions.Count > 0 ? string.Join(", ", chatRoom.SuggestQuestions) : "無")}");
                                        Console.WriteLine($"  最新聊天記錄ID: {chatRoom.LatestChatLogId}");
                                        if (chatRoom.SearchResults != null && chatRoom.SearchResults.Count > 0)
                                        {
                                            Console.WriteLine($"  搜尋結果:");
                                            int idx = 1;
                                            foreach (var r in chatRoom.SearchResults)
                                            {
                                                Console.WriteLine($"    - 結果 {idx++}:");
                                                Console.WriteLine($"      文件名稱(doc_name): {r.DocName}");
                                                Console.WriteLine($"      文件ID(document_id): {r.DocumentId}");
                                                Console.WriteLine($"      區塊索引(chunk_index): {r.ChunkIndex}");
                                                Console.WriteLine($"      資料來源(data_source): {r.DataSource}");
                                                Console.WriteLine($"      索引名稱(index): {r.Index}");
                                                Console.WriteLine($"      搜索模式(search_mode): {r.SearchMode}");
                                                Console.WriteLine($"      最後修改時間(last_modified): {r.LastModified:yyyy-MM-dd HH:mm:ss}");
                                                Console.WriteLine($"      分數(score): {r.Score}");
                                                if (r.Document != null && r.Document.Count > 0)
                                                {
                                                    Console.WriteLine($"      文件內容(document):");
                                                    foreach (var docField in r.Document)
                                                    {
                                                        Console.WriteLine($"        - {docField.Key}: {docField.Value}");
                                                    }
                                                }
                                                else
                                                {
                                                    Console.WriteLine($"      文件內容(document): 無");
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            case "end":
                                Console.WriteLine("\n[對話結束]");
                                goto EndStream;
                            case "error":
                                Console.WriteLine($"\n[錯誤] {chunk.Data?.GetValue("error")}");
                                break;
                        }
                    }
                    catch (JsonException) { }
                }
            }
            EndStream:
            Console.WriteLine($"\n完整回應長度: {fullMessage.Length} 字元");
            return (returnedChatRoomId, latestChatLogId);
        }
        else
        {
            await HandleErrorResponse(response);
            return (null, null);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ 聊天請求錯誤: {ex.Message}");
        return (null, null);
    }
}

// 取得聊天室列表
async Task<List<ChatRoom>?> GetChatRooms()
{
    Console.WriteLine("[聊天室列表]");
    var url = $"{baseUrl}/api/chat/chatrooms";
    try
    {
        client.DefaultRequestHeaders.Clear();
        var response = await client.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<JsonResponse<List<ChatRoom>>>(content);
            if (result?.Error == false && result.JsonData != null)
            {
                Console.WriteLine($"✓ 找到 {result.JsonData.Count} 個聊天室:");
                foreach (var chatRoom in result.JsonData)
                {
                    Console.WriteLine("------------------------------");
                    Console.WriteLine($"  聊天室ID: {chatRoom.Id}");
                    Console.WriteLine($"  聊天室標題: {chatRoom.Title}");
                    Console.WriteLine($"  聊天室描述: {chatRoom.Description}");
                    Console.WriteLine($"  機器人身份設定: {chatRoom.Role}");
                    Console.WriteLine($"  產品系統提示詞: {chatRoom.ProductSystemPrompt}");
                    Console.WriteLine($"  狀態: {chatRoom.Status}");
                    Console.WriteLine($"  使用的AI模型名稱: {chatRoom.ModelName}");
                    Console.WriteLine($"  使用的搜索結果數量: {chatRoom.SearchSelectedNumber}");
                    Console.WriteLine($"  檢索的總結果數量: {chatRoom.SearchTotalNumber}");
                    Console.WriteLine($"  詞與向量搜索的比例: {chatRoom.DataSourceRatio}");
                    Console.WriteLine($"  知識使用模式: {chatRoom.UseKnowledgeMode}");
                    Console.WriteLine($"  是否重新排序結果: {chatRoom.EnableRerank}");
                    Console.WriteLine($"  記住的歷史消息數量: {chatRoom.MemoryCount}");
                    Console.WriteLine($"  回應格式: {chatRoom.ResponseFormat}");
                    Console.WriteLine($"  是否啟用系統推薦問題: {chatRoom.EnableSuggestQuestions}");
                    Console.WriteLine($"  AI回應的創造性/隨機性: {chatRoom.Temperature}");
                    Console.WriteLine($"  文件欄位映射關係: {(chatRoom.DocumentFieldMapping != null && chatRoom.DocumentFieldMapping.Count > 0 ? string.Join(", ", chatRoom.DocumentFieldMapping.Select(kv => $"{kv.Key}:{kv.Value}")) : "無")}");
                    Console.WriteLine($"  聊天記錄數量: {chatRoom.ChatLogsCount}");
                    Console.WriteLine($"  建立時間: {chatRoom.CreatedAt}");
                    Console.WriteLine($"  更新時間: {chatRoom.UpdatedAt}");
                    Console.WriteLine($"  選擇的文件索引列表: {(chatRoom.SelectedIndex != null && chatRoom.SelectedIndex.Count > 0 ? string.Join(", ", chatRoom.SelectedIndex) : "無")}");
                    Console.WriteLine($"  建議問題: {(chatRoom.SuggestQuestions != null && chatRoom.SuggestQuestions.Count > 0 ? string.Join(", ", chatRoom.SuggestQuestions) : "無")}");
                    Console.WriteLine($"  最新聊天記錄ID: {chatRoom.LatestChatLogId}");
                    if (chatRoom.SearchResults != null && chatRoom.SearchResults.Count > 0)
                    {
                        Console.WriteLine($"  搜尋結果:");
                        int idx = 1;
                        foreach (var r in chatRoom.SearchResults)
                        {
                            Console.WriteLine($"    - 結果 {idx++}:");
                            Console.WriteLine($"      文件名稱(doc_name): {r.DocName}");
                            Console.WriteLine($"      文件ID(document_id): {r.DocumentId}");
                            Console.WriteLine($"      區塊索引(chunk_index): {r.ChunkIndex}");
                            Console.WriteLine($"      資料來源(data_source): {r.DataSource}");
                            Console.WriteLine($"      索引名稱(index): {r.Index}");
                            Console.WriteLine($"      搜索模式(search_mode): {r.SearchMode}");
                            Console.WriteLine($"      最後修改時間(last_modified): {r.LastModified:yyyy-MM-dd HH:mm:ss}");
                            Console.WriteLine($"      分數(score): {r.Score}");
                            if (r.Document != null && r.Document.Count > 0)
                            {
                                Console.WriteLine($"      文件內容(document):");
                                foreach (var docField in r.Document)
                                {
                                    Console.WriteLine($"        - {docField.Key}: {docField.Value}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"      文件內容(document): 無");
                            }
                        }
                    }
                }
                return result.JsonData;
            }
            else
            {
                Console.WriteLine($"✗ 取得聊天室列表失敗: {result?.Message}");
            }
        }
        else
        {
            await HandleErrorResponse(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ 取得聊天室列表錯誤: {ex.Message}");
    }
    return null;
}

// 取得聊天記錄列表
async Task<List<ChatLog>?> GetChatLogs(int chatRoomId)
{
    Console.WriteLine($"[聊天記錄查詢] (聊天室ID: {chatRoomId})");
    var url = $"{baseUrl}/api/chat/chatrooms/{chatRoomId}/chatlogs";
    try
    {
        client.DefaultRequestHeaders.Clear();
        var response = await client.GetAsync(url);
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<JsonResponse<List<ChatLog>>>(content);
            if (result?.Error == false && result.JsonData != null)
            {
                Console.WriteLine($"✓ 找到 {result.JsonData.Count} 筆聊天記錄:");
                foreach (var log in result.JsonData)
                {
                    Console.WriteLine("------------------------------");
                    Console.WriteLine($"  記錄ID: {log.Id}");
                    Console.WriteLine($"  所屬聊天室ID: {log.ChatRoomId}");
                    Console.WriteLine($"  前一筆聊天記錄ID: {(log.PreviousChatLogId.HasValue ? log.PreviousChatLogId.ToString() : "無")}");
                    Console.WriteLine($"  使用者問題: {log.HumanContent}");
                    Console.WriteLine($"  AI 回答: {log.AiContent}");
                    Console.WriteLine($"  使用者發問時間: {log.HumanTime:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"  AI 回答時間: {(log.AiTime.HasValue ? log.AiTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  建議問題: {(log.SuggestQuestions != null && log.SuggestQuestions.Count > 0 ? string.Join(", ", log.SuggestQuestions) : "無")}");
                    // 更細緻列出搜尋結果
                    if (log.SearchResults != null && log.SearchResults.Count > 0)
                    {
                        Console.WriteLine($"  搜尋結果:");
                        int idx = 1;
                        foreach (var r in log.SearchResults)
                        {
                            Console.WriteLine($"    - 結果 {idx++}:");
                            Console.WriteLine($"      文件名稱(doc_name): {r.DocName}");
                            Console.WriteLine($"      文件ID(document_id): {r.DocumentId}");
                            Console.WriteLine($"      區塊索引(chunk_index): {r.ChunkIndex}");
                            Console.WriteLine($"      資料來源(data_source): {r.DataSource}");
                            Console.WriteLine($"      索引名稱(index): {r.Index}");
                            Console.WriteLine($"      搜索模式(search_mode): {r.SearchMode}");
                            Console.WriteLine($"      最後修改時間(last_modified): {r.LastModified:yyyy-MM-dd HH:mm:ss}");
                            Console.WriteLine($"      分數(score): {r.Score}");
                            if (r.Document != null && r.Document.Count > 0)
                            {
                                Console.WriteLine($"      文件內容(document):");
                                foreach (var docField in r.Document)
                                {
                                    Console.WriteLine($"        - {docField.Key}: {docField.Value}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"      文件內容(document): 無");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"  搜尋結果: 無");
                    }
                    Console.WriteLine($"  語言: {log.Language}");
                    Console.WriteLine($"  是否為程式碼問題: {(log.IsCoding ? "是" : "否")}");
                    Console.WriteLine($"  查詢開始時間: {(log.QueryStartTime.HasValue ? log.QueryStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  查詢結束時間: {(log.QueryEndTime.HasValue ? log.QueryEndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  關鍵字: {(log.Keywords != null && log.Keywords.Count > 0 ? string.Join(", ", log.Keywords) : "無")}");
                    Console.WriteLine($"  問題標準化: {(string.IsNullOrEmpty(log.Question) ? "無" : log.Question)}");
                    Console.WriteLine();
                }
                return result.JsonData;
            }
            else
            {
                Console.WriteLine($"✗ 取得聊天記錄失敗: {result?.Message}");
            }
        }
        else
        {
            await HandleErrorResponse(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ 取得聊天記錄錯誤: {ex.Message}");
    }
    return null;
}

// 為聊天記錄評價
async Task RateChatLog(int chatLogId)
{
    Console.WriteLine($"[聊天記錄評價] (記錄ID: {chatLogId})");
    var url = $"{baseUrl}/api/chat/chat_logs/{chatLogId}/rating";
    var ratingRequest = new RatingRequest
    {
        RatingType = "positive",
        Feedback = "這個回答很有幫助！"
    };
    var jsonContent = JsonConvert.SerializeObject(ratingRequest);
    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
    try
    {
        client.DefaultRequestHeaders.Clear();
        var response = await client.PostAsync(url, content);
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<JsonResponse<object>>(responseContent);
            if (result?.Error == false)
            {
                Console.WriteLine($"✓ 評價提交成功: {result.Message}");
            }
            else
            {
                Console.WriteLine($"✗ 評價提交失敗: {result?.Message}");
            }
        }
        else
        {
            await HandleErrorResponse(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ 評價提交錯誤: {ex.Message}");
    }
}

// 錯誤處理
async Task HandleErrorResponse(HttpResponseMessage response)
{
    try
    {
        var errorContent = await response.Content.ReadAsStringAsync();
        var error = JsonConvert.DeserializeObject<JsonResponse<object>>(errorContent);
        Console.WriteLine($"✗ API 錯誤: {error?.Message} (代碼: {error?.Code}, HTTP: {response.StatusCode})");
    }
    catch
    {
        Console.WriteLine($"✗ HTTP 錯誤: {response.StatusCode}");
    }
}

// --- 資料結構定義 ---

public class ChatRequest
{
    [JsonProperty("chat_room_id")]
    public int? ChatRoomId { get; set; }
    [JsonProperty("chat_log_id")]
    public int? ChatLogId { get; set; }
    [JsonProperty("human_content")]
    public string HumanContent { get; set; }
    [JsonProperty("config_name")]
    public string ConfigName { get; set; }
    [JsonProperty("user_id")]
    public string? UserId { get; set; }
}

public class StreamChunk
{
    [JsonProperty("chunk_type")]
    public string ChunkType { get; set; }
    [JsonProperty("content")]
    public string Content { get; set; }
    [JsonProperty("data")]
    public dynamic? Data { get; set; }
}

public class JsonResponse<T>
{
    [JsonProperty("json_data")]
    public T? JsonData { get; set; }
    [JsonProperty("error")]
    public bool Error { get; set; }
    [JsonProperty("message")]
    public string Message { get; set; }
    [JsonProperty("code")]
    public int Code { get; set; }
    [JsonProperty("http_status")]
    public int HttpStatus { get; set; }
}

public class ChatRoom
{
    [JsonProperty("id")]
    public int Id { get; set; }
    [JsonProperty("title")]
    public string Title { get; set; }
    [JsonProperty("description")]
    public string Description { get; set; }
    [JsonProperty("role")]
    public string Role { get; set; }
    [JsonProperty("product_system_prompt")]
    public string ProductSystemPrompt { get; set; }
    [JsonProperty("status")]
    public string Status { get; set; }
    [JsonProperty("model_name")]
    public string ModelName { get; set; }
    [JsonProperty("search_selected_number")]
    public int SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number")]
    public int SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio")]
    public float DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode")]
    public string UseKnowledgeMode { get; set; }
    [JsonProperty("enable_rerank")]
    public bool EnableRerank { get; set; }
    [JsonProperty("memory_count")]
    public int MemoryCount { get; set; }
    [JsonProperty("response_format")]
    public string ResponseFormat { get; set; }
    [JsonProperty("enable_suggest_questions")]
    public bool EnableSuggestQuestions { get; set; }
    [JsonProperty("temperature")]
    public float Temperature { get; set; }
    [JsonProperty("document_field_mapping")]
    public Dictionary<string, string>? DocumentFieldMapping { get; set; }
    [JsonProperty("chat_logs_count")]
    public int ChatLogsCount { get; set; }
    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }
    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }
    [JsonProperty("suggest_questions")]
    public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")]
    public List<DocumentResult>? SearchResults { get; set; }
    [JsonProperty("latest_chat_log_id")]
    public int? LatestChatLogId { get; set; }
    [JsonProperty("selected_index")]
    public List<string>? SelectedIndex { get; set; }
}

public class ChatLog
{
    [JsonProperty("id")]
    public int Id { get; set; }
    [JsonProperty("chat_room_id")]
    public int ChatRoomId { get; set; }
    [JsonProperty("previous_chat_log_id")]
    public int? PreviousChatLogId { get; set; }
    [JsonProperty("human_content")]
    public string HumanContent { get; set; }
    [JsonProperty("ai_content")]
    public string? AiContent { get; set; }
    [JsonProperty("human_time")]
    public DateTime HumanTime { get; set; }
    [JsonProperty("ai_time")]
    public DateTime? AiTime { get; set; }
    [JsonProperty("suggest_questions")]
    public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")]
    public List<DocumentResult>? SearchResults { get; set; }
    [JsonProperty("language")]
    public string Language { get; set; }
    [JsonProperty("is_coding")]
    public bool IsCoding { get; set; }
    [JsonProperty("query_start_time")]
    public DateTime? QueryStartTime { get; set; }
    [JsonProperty("query_end_time")]
    public DateTime? QueryEndTime { get; set; }
    [JsonProperty("keywords")]
    public List<string>? Keywords { get; set; }
    [JsonProperty("question")]
    public string? Question { get; set; }
}

public class RatingRequest
{
    [JsonProperty("rating_type")]
    public string RatingType { get; set; }
    [JsonProperty("feedback")]
    public string? Feedback { get; set; }
}

public class ConfigRequest
{
    [JsonProperty("product_system_prompt")]
    public string ProductSystemPrompt { get; set; }
    [JsonProperty("role")]
    public string Role { get; set; }
    [JsonProperty("model_name")]
    public string ModelName { get; set; }
    [JsonProperty("search_selected_number")]
    public int SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number")]
    public int SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio")]
    public float DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode")]
    public string UseKnowledgeMode { get; set; }
    [JsonProperty("enable_rerank")]
    public bool EnableRerank { get; set; }
    [JsonProperty("memory_count")]
    public int MemoryCount { get; set; }
    [JsonProperty("enable_suggest_questions")]
    public bool EnableSuggestQuestions { get; set; }
    [JsonProperty("response_format")]
    public string ResponseFormat { get; set; }
    [JsonProperty("document_field_mapping")]
    public Dictionary<string, string>? DocumentFieldMapping { get; set; }
    [JsonProperty("selected_index")]
    public List<string>? SelectedIndex { get; set; }
}

public class DocumentResult
{
    [JsonProperty("doc_name")]
    public string DocName { get; set; }

    [JsonProperty("document_id")]
    public string DocumentId { get; set; }

    [JsonProperty("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonProperty("data_source")]
    public string DataSource { get; set; }

    [JsonProperty("index")]
    public string Index { get; set; }

    [JsonProperty("search_mode")]
    public string SearchMode { get; set; }

    [JsonProperty("last_modified")]
    public DateTime LastModified { get; set; }

    [JsonProperty("score")]
    public float Score { get; set; }

    [JsonProperty("document")]
    public Dictionary<string, object> Document { get; set; }
}

public class ChatConfig
{
    public string? ChatroomSystemPrompt { get; set; } // 聊天室系統提示詞
    public string? ProductSystemPrompt { get; set; } // 產品系統提示詞
    public string? IntensionSystemPrompt { get; set; } // 意圖系統提示詞
    public string Role { get; set; } // 機器人身份設定
    public string ModelName { get; set; } // 使用的AI模型名稱
    public int SearchSelectedNumber { get; set; } // 實際用於上下文的搜索結果數量
    public int SearchTotalNumber { get; set; } // 檢索的總搜索結果數量
    public float DataSourceRatio { get; set; } // 詞向量搜索比例 (0.0=純向量, 1.0=純關鍵字)
    public string UseKnowledgeMode { get; set; } // 知識使用模式：none/assist/strict
    public bool EnableRerank { get; set; } // 是否重新排序搜索結果
    public int MemoryCount { get; set; } // 記住的歷史對話數量
    public bool EnableSuggestQuestions { get; set; } // 是否啟用推薦問題
    public string ResponseFormat { get; set; } // 回應格式：markdown/html
    public float Temperature { get; set; } // AI回應的創造性/隨機性
    public string? Timezone { get; set; } // 時區
    public Dictionary<string, string>? DocumentFieldMapping { get; set; } // 文件欄位映射表
    public List<string>? SelectedIndex { get; set; } // 選擇的文件索引列表

    public static void Print(ChatConfig config)
    {
        Console.WriteLine("【配置資訊】");
        Console.WriteLine($"  聊天室系統提示詞: {config.ChatroomSystemPrompt ?? "(無)"}");
        Console.WriteLine($"  產品系統提示詞: {config.ProductSystemPrompt ?? "(無)"}");
        Console.WriteLine($"  意圖系統提示詞: {config.IntensionSystemPrompt ?? "(無)"}");
        Console.WriteLine($"  機器人身份設定: {config.Role}");
        Console.WriteLine($"  使用的AI模型名稱: {config.ModelName}");
        Console.WriteLine($"  實際用於上下文的搜索結果數量: {config.SearchSelectedNumber}");
        Console.WriteLine($"  檢索的總搜索結果數量: {config.SearchTotalNumber}");
        Console.WriteLine($"  詞向量搜索比例: {config.DataSourceRatio}");
        Console.WriteLine($"  知識使用模式: {config.UseKnowledgeMode}");
        Console.WriteLine($"  是否重新排序搜索結果: {(config.EnableRerank ? "是" : "否")}");
        Console.WriteLine($"  記住的歷史對話數量: {config.MemoryCount}");
        Console.WriteLine($"  是否啟用推薦問題: {(config.EnableSuggestQuestions ? "是" : "否")}");
        Console.WriteLine($"  回應格式: {config.ResponseFormat}");
        Console.WriteLine($"  AI回應的創造性/隨機性: {config.Temperature}");
        Console.WriteLine($"  時區: {config.Timezone ?? "(無)"}");
        Console.WriteLine($"  文件欄位映射表: {(config.DocumentFieldMapping != null && config.DocumentFieldMapping.Count > 0 ? string.Join(", ", config.DocumentFieldMapping.Select(kv => $"{kv.Key}:{kv.Value}")) : "無")}");
        Console.WriteLine($"  選擇的文件索引列表: {(config.SelectedIndex != null && config.SelectedIndex.Count > 0 ? string.Join(", ", config.SelectedIndex) : "無")}");
    }
}