using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Text;

// 共用HttpClient
var handler = new HttpClientHandler();
handler.ServerCertificateCustomValidationCallback =
    (httpRequestMessage, cert, cetChain, policyErrors) =>
    {
        return true;
    };
HttpClient client = new HttpClient(handler); // 如果碰到SSL憑證問題，可能可以嘗試加上或拿掉handler

// 設定API基本資訊
string baseUrl = "http://localhost:8000"; // GufoRAG API 基礎URL
string configName = "default"; // 配置名稱

// 示範不同的Chat API功能
Console.WriteLine("=== GufoRAG Chat API 示範程式 ===");
Console.WriteLine();

// 1. 聊天對話API (SSE串流)
var chatRequest = new ChatRequest
{
    ChatRoomId = null, // null表示新建聊天室
    ChatLogId = null,  // null表示新建記錄
    HumanContent = "請問什麼是人工智慧？", // 使用者輸入內容
    ConfigName = configName
};

int? chatRoomId = await ChatWithBot(chatRequest);
Console.WriteLine();

// 2. 取得聊天室列表
await GetChatRooms();
Console.WriteLine();

// 3. 取得聊天記錄列表 (如果有聊天室ID)
if (chatRoomId.HasValue)
{
    await GetChatLogs(chatRoomId.Value);
    Console.WriteLine();
}

// 4. 評價功能示範 (如果有聊天記錄)
if (chatRoomId.HasValue)
{
    // 先取得聊天記錄來找到可以評價的記錄
    var chatLogId = await GetFirstChatLogId(chatRoomId.Value);
    if (chatLogId.HasValue)
    {
        await RateChatLog(chatLogId.Value);
        Console.WriteLine();
        
        await GetChatLogRating(chatLogId.Value);
        Console.WriteLine();
    }
}

// 聊天對話API (SSE串流)
async Task<int?> ChatWithBot(ChatRequest chatRequest)
{
    Console.WriteLine("1. 聊天對話API (串流回應):");
    Console.WriteLine($"使用者輸入: {chatRequest.HumanContent}");
    Console.WriteLine("AI回應:");
    
    var url = $"{baseUrl}/api/chat/chatbot";
    var jsonContent = JsonConvert.SerializeObject(chatRequest);
    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
    
    // 清除標頭，只保留必要的Content-Type
    client.DefaultRequestHeaders.Clear();
    
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        
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
                                Console.Out.Flush(); // 強制立即輸出
                                fullMessage.Append(chunk.Content);
                                break;
                                
                            case "chat_room":
                                var chatRoomData = chunk.Data;
                                if (chatRoomData != null)
                                {
                                    var id = chatRoomData.GetValue("id");
                                    var title = chatRoomData.GetValue("title");
                                    var role = chatRoomData.GetValue("role");
                                    var modelName = chatRoomData.GetValue("model_name");
                                    returnedChatRoomId = id?.ToObject<int>();
                                    
                                    Console.WriteLine($"\n[聊天室資訊] ID: {id}, 標題: {title}, 角色: {role}, 模型: {modelName}");
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
                    catch (JsonException)
                    {
                        // 忽略無法解析的資料行
                    }
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
async Task GetChatRooms()
{
    Console.WriteLine("2. 聊天室列表API:");
    
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
}

// 取得聊天記錄列表
async Task GetChatLogs(int chatRoomId)
{
    Console.WriteLine($"3. 聊天記錄查詢API (聊天室ID: {chatRoomId}):");
    
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
                    Console.WriteLine($"    AI: {log.AiContent?.Substring(0, Math.Min(50, log.AiContent.Length))}...");
                    Console.WriteLine($"    時間: {log.HumanTime:yyyy-MM-dd HH:mm:ss}");
                    if (log.SuggestQuestions?.Any() == true)
                    {
                        Console.WriteLine($"    建議問題: {string.Join(", ", log.SuggestQuestions)}");
                    }
                    Console.WriteLine();
                }
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
}

// 為聊天記錄評價
async Task RateChatLog(int chatLogId)
{
    Console.WriteLine($"4. 聊天記錄評價API (記錄ID: {chatLogId}):");
    
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

// 查詢聊天記錄評價
async Task GetChatLogRating(int chatLogId)
{
    Console.WriteLine($"5. 查詢聊天記錄評價API (記錄ID: {chatLogId}):");
    
    var url = $"{baseUrl}/api/chat/chat_logs/{chatLogId}/rating";
    
    try
    {
        client.DefaultRequestHeaders.Clear();
        
        var response = await client.GetAsync(url);
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<ApiResponse<dynamic>>(content);
            
            if (result?.Error == false && result.JsonData != null)
            {
                var data = result.JsonData;
                Console.WriteLine($"✓ 評價資訊:");
                Console.WriteLine($"  - 評價類型: {data.rating_type}");
                Console.WriteLine($"  - 評價回饋: {data.rating_feedback}");
                Console.WriteLine($"  - 評價時間: {data.rating_time}");
                Console.WriteLine($"  - 是否有評價: {data.has_rating}");
            }
            else
            {
                Console.WriteLine($"✗ 取得評價失敗: {result?.Message}");
            }
        }
        else
        {
            await HandleErrorResponse(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"✗ 取得評價錯誤: {ex.Message}");
    }
}

// 取得第一個聊天記錄ID (輔助函數)
async Task<int?> GetFirstChatLogId(int chatRoomId)
{
    var url = $"{baseUrl}/api/chat/chatrooms/{chatRoomId}/chatlogs";
    
    try
    {
        client.DefaultRequestHeaders.Clear();
        
        var response = await client.GetAsync(url);
        
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<ApiResponse<List<ChatLog>>>(content);
            
            if (result?.Error == false && result.JsonData?.Any() == true)
            {
                return result.JsonData.First().Id;
            }
        }
    }
    catch (Exception)
    {
        // 靜默處理錯誤
    }
    
    return null;
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

// 資料結構定義

// 聊天請求
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
}

// 串流資料塊
public class StreamChunk
{
    [JsonProperty("chunk_type")]
    public string ChunkType { get; set; } = string.Empty;
    
    [JsonProperty("content")]
    public string Content { get; set; } = string.Empty;
    
    [JsonProperty("data")]
    public dynamic? Data { get; set; }
}

// API回應格式
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

// 聊天室
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
    
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;
    
    [JsonProperty("model_name")]
    public string ModelName { get; set; } = string.Empty;
    
    [JsonProperty("chat_logs_count")]
    public int ChatLogsCount { get; set; }
    
    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }
    
    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

// 聊天記錄
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

// 評價請求
public class RatingRequest
{
    [JsonProperty("rating_type")]
    public string RatingType { get; set; } = string.Empty; // "positive" 或 "negative"
    
    [JsonProperty("feedback")]
    public string? Feedback { get; set; }
}