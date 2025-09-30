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
    // 1. 新增 config
    var newConfig = new ConfigRequest
    {
        Role = "測試助手",
        ModelName = "openai:gpt-4o",
        ProductSystemPrompt = "你是測試用助手。",
        SearchSelectedNumber = 3,
        SearchTotalNumber = 6,
        EnableSuggestQuestions = true,
        UseKnowledgeMode = "strict"
    };
    var createConfigRes = await PostJson($"{baseUrl}/api/config/{configName}", newConfig);
    Console.WriteLine($"[新增 config] {createConfigRes}\n");
    await Task.Delay(5000); // 暫停5秒鐘

    // 2. 查詢 config 列表
    var configListRes = await Get($"{baseUrl}/api/config/list");
    Console.WriteLine($"[查詢 config 列表] {configListRes}\n");
    await Task.Delay(5000); // 暫停5秒鐘

    // 3. 刪除 config
    var deleteConfigRes = await Delete($"{baseUrl}/api/config/{configName}");
    Console.WriteLine($"[刪除 config] {deleteConfigRes}\n");
    await Task.Delay(5000); // 暫停5秒鐘

    // 4. 問答 (SSE串流)
    var chatRequest = new ChatRequest
    {
        ChatRoomId = null,
        ChatLogId = null,
        HumanContent = "請問什麼是人工智慧？",
        ConfigName = "default"
    };
    int? chatRoomId = await ChatWithBot(chatRequest);
    Console.WriteLine();
    await Task.Delay(5000); // 暫停5秒鐘

    // 5. 查詢聊天室列表
    var chatRooms = await GetChatRooms();
    Console.WriteLine();
    await Task.Delay(5000); // 暫停5秒鐘

    // 6. 查詢聊天紀錄 (取第一個聊天室)
    int? firstRoomId = chatRooms?.Count > 0 ? chatRooms[0].Id : null;
    if (firstRoomId.HasValue)
    {
        var chatLogs = await GetChatLogs(firstRoomId.Value);
        Console.WriteLine();
        await Task.Delay(5000); // 暫停5秒鐘

        // 7. 上傳評論 (取第一筆聊天紀錄)
        int? firstLogId = chatLogs?.Count > 0 ? chatLogs[0].Id : null;
        if (firstLogId.HasValue)
        {
            await RateChatLog(firstLogId.Value);
            Console.WriteLine();
            await Task.Delay(5000); // 暫停5秒鐘
        }
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
async Task<int?> ChatWithBot(ChatRequest chatRequest)
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
                                var chatRoomData = chunk.Data;
                                if (chatRoomData != null)
                                {
                                    var id = chatRoomData.GetValue("id");
                                    returnedChatRoomId = id?.ToObject<int>();
                                    Console.WriteLine($"\n[聊天室資訊] ID: {id}");
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
            return returnedChatRoomId;
        }
        else
        {
            await HandleErrorResponse(response);
            return null;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ 聊天請求錯誤: {ex.Message}");
        return null;
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
            var result = JsonConvert.DeserializeObject<ApiResponse<List<ChatRoom>>>(content);
            if (result?.Error == false && result.JsonData != null)
            {
                Console.WriteLine($"✓ 找到 {result.JsonData.Count} 個聊天室:");
                foreach (var room in result.JsonData)
                {
                    Console.WriteLine($"  - ID: {room.Id}, 標題: {room.Title}, 角色: {room.Role}, 模型: {room.ModelName}, 記錄數: {room.ChatLogsCount}");
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
            var result = JsonConvert.DeserializeObject<ApiResponse<List<ChatLog>>>(content);
            if (result?.Error == false && result.JsonData != null)
            {
                Console.WriteLine($"✓ 找到 {result.JsonData.Count} 筆聊天記錄:");
                foreach (var log in result.JsonData)
                {
                    Console.WriteLine($"  - 記錄ID: {log.Id}");
                    Console.WriteLine($"    使用者: {log.HumanContent}");
                    Console.WriteLine($"    AI: {log.AiContent?.Substring(0, Math.Min(50, log.AiContent?.Length ?? 0))}...");
                    Console.WriteLine($"    時間: {log.HumanTime:yyyy-MM-dd HH:mm:ss}");
                    if (log.SuggestQuestions?.Any() == true)
                    {
                        Console.WriteLine($"    建議問題: {string.Join(", ", log.SuggestQuestions)}");
                    }
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
            var result = JsonConvert.DeserializeObject<ApiResponse<object>>(responseContent);
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
        var error = JsonConvert.DeserializeObject<ApiResponse<object>>(errorContent);
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
    public string HumanContent { get; set; } = string.Empty;
    [JsonProperty("config_name")]
    public string ConfigName { get; set; } = "default";
    [JsonProperty("user_id")]
    public string? UserId { get; set; }
}

public class StreamChunk
{
    [JsonProperty("chunk_type")]
    public string ChunkType { get; set; } = string.Empty;
    [JsonProperty("content")]
    public string Content { get; set; } = string.Empty;
    [JsonProperty("data")]
    public dynamic? Data { get; set; }
}

public class ApiResponse<T>
{
    [JsonProperty("json_data")]
    public T? JsonData { get; set; }
    [JsonProperty("error")]
    public bool Error { get; set; }
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
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
    public string Title { get; set; } = string.Empty;
    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;
    [JsonProperty("role")]
    public string Role { get; set; } = string.Empty;
    [JsonProperty("chatroom_system_prompt")]
    public string ChatroomSystemPrompt { get; set; } = string.Empty;
    [JsonProperty("product_system_prompt")]
    public string ProductSystemPrompt { get; set; } = string.Empty;
    [JsonProperty("intension_system_prompt")]
    public string IntensionSystemPrompt { get; set; } = string.Empty;
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;
    [JsonProperty("model_name")]
    public string ModelName { get; set; } = string.Empty;
    [JsonProperty("active_chain_end_id")]
    public int? ActiveChainEndId { get; set; }
    [JsonProperty("search_selected_number")]
    public int SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number")]
    public int SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio")]
    public float DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode")]
    public string UseKnowledgeMode { get; set; } = string.Empty;
    [JsonProperty("enable_rerank")]
    public bool EnableRerank { get; set; }
    [JsonProperty("memory_count")]
    public int MemoryCount { get; set; }
    [JsonProperty("response_format")]
    public string ResponseFormat { get; set; } = string.Empty;
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
    public string HumanContent { get; set; } = string.Empty;
    [JsonProperty("ai_content")]
    public string? AiContent { get; set; }
    [JsonProperty("human_time")]
    public DateTime HumanTime { get; set; }
    [JsonProperty("ai_time")]
    public DateTime? AiTime { get; set; }
    [JsonProperty("suggest_questions")]
    public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")]
    public List<object>? SearchResults { get; set; }
    [JsonProperty("language")]
    public string Language { get; set; } = string.Empty;
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
    public string RatingType { get; set; } = string.Empty;
    [JsonProperty("feedback")]
    public string? Feedback { get; set; }
}

public class ConfigRequest
{
    [JsonProperty("product_system_prompt")]
    public string ProductSystemPrompt { get; set; } = string.Empty;
    [JsonProperty("role")]
    public string Role { get; set; } = string.Empty;
    [JsonProperty("model_name")]
    public string ModelName { get; set; } = string.Empty;
    [JsonProperty("search_selected_number")]
    public int SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number")]
    public int SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio")]
    public float DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode")]
    public string UseKnowledgeMode { get; set; } = string.Empty;
    [JsonProperty("enable_rerank")]
    public bool EnableRerank { get; set; }
    [JsonProperty("memory_count")]
    public int MemoryCount { get; set; }
    [JsonProperty("enable_suggest_questions")]
    public bool EnableSuggestQuestions { get; set; }
    [JsonProperty("response_format")]
    public string ResponseFormat { get; set; } = string.Empty;
    [JsonProperty("document_field_mapping")]
    public Dictionary<string, string>? DocumentFieldMapping { get; set; }
    [JsonProperty("selected_index")]
    public List<string>? SelectedIndex { get; set; }
}