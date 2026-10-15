using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

// 共用HttpClient
var handler = new HttpClientHandler();
handler.ServerCertificateCustomValidationCallback =
    (httpRequestMessage, cert, cetChain, policyErrors) => true;
HttpClient client = new HttpClient(handler); // 如果碰到SSL憑證問題，可能可以嘗試加上或拿掉handler
// SSE 串流可能持續數十秒以上（檢索 + LLM 生成），預設 100 秒逾時不夠用
client.Timeout = TimeSpan.FromMinutes(10);

string baseUrl = "http://localhost:5555";
string configName = "demo_config"; // 新增用的 config 名稱（不可用保留字：default/list/models/rerankers/builtin-tools/limits）

Console.WriteLine("=== GufoRAG API 全流程範例 ===\n");

try
{
    // 示範config相關流程(設定主題)
    // 1. 新增 config（只需送要設定的欄位，其餘用預設值；未知欄位會回 422）
    var newConfig = new ConfigRequest
    {
        // 系統提示詞
        ProductSystemPrompt = "你是測試用助手。",
        // 機器人身份設定
        Role = "測試助手",
        // 使用的AI模型名稱（須為 GET /api/config/models 清單內的模型）
        ModelName = "openai:gpt-5-mini",
        // 實際用於上下文的搜索結果數量
        SearchSelectedNumber = 3,
        // 檢索的總搜索結果數量
        SearchTotalNumber = 6,
        // 詞向量搜索比例 (0.0=純向量, 1.0=純關鍵字)
        DataSourceRatio = 0.0f,
        // 知識使用模式：none/assist/strict (使用模型自有知識/參考資料+模型自有知識/限用參考資料)
        UseKnowledgeMode = "strict",
        // 是否重新排序搜索結果（設 true 時須同時指定 reranker_name，可選值見 GET /api/config/rerankers）
        EnableRerank = false,
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
        // 注意：建立回應的 json_data 只包含「本次送出的欄位」，要看完整設定請再 GET /api/config/{config_name}
        var createConfigObj = JsonConvert.DeserializeObject<JsonResponse<ChatConfig>>(createConfigRes);
        if (createConfigObj?.Error == false && createConfigObj.JsonData != null)
        {
            ChatConfig.Print(createConfigObj.JsonData);
            PrintWarnings(createConfigObj.Warnings);
        }
        else
        {
            PrintApiError(createConfigObj);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[反序列化失敗] {ex.Message}");
    }
    await Task.Delay(5000); // 暫停5秒鐘

    // 2. 查詢 config 列表（分頁：不帶 limit 只回前 100 筆，上限 1000；沒有 total_count，需自行翻頁）
    var configListRes = await Get($"{baseUrl}/api/config/list?limit=1000&offset=0");
    Console.WriteLine($"[查詢 config 列表] 共 {configListRes.Length} 字元\n");
    try
    {
        var configListObj = JsonConvert.DeserializeObject<JsonResponse<List<ChatConfig>>>(configListRes);
        if (configListObj?.Error == false && configListObj.JsonData != null && configListObj.JsonData.Count > 0)
        {
            foreach (var config in configListObj.JsonData)
            {
                ChatConfig.Print(config);
                Console.WriteLine();
            }
        }
        else
        {
            PrintApiError(configListObj);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[反序列化失敗] {ex.Message}");
    }
    await Task.Delay(5000); // 暫停5秒鐘

    // 3. 刪除 config（不可刪除 default）
    var deleteConfigRes = await Delete($"{baseUrl}/api/config/{configName}");
    Console.WriteLine($"[刪除 config] {deleteConfigRes}\n");
    try
    {
        var deleteConfigObj = JsonConvert.DeserializeObject<JsonResponse<JObject>>(deleteConfigRes);
        var deletedConfigName = deleteConfigObj?.JsonData?["deleted_config"]?.ToString();
        if (deleteConfigObj?.Error == false && !string.IsNullOrEmpty(deletedConfigName))
        {
            Console.WriteLine($"已刪除 config: {deletedConfigName}");
        }
        else
        {
            PrintApiError(deleteConfigObj);
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
        ChatRoomId = null, // null = 新建聊天室；續談請帶上次 chat_room 塊回傳的 id
        ChatLogId = null,
        HumanContent = "請問什麼是人工智慧？", // 1-2000 字
        ConfigName = "default",
        UserId = "test_user",
        // 篩選條件改用 DSL 字串（取代舊的 contains_any / privileges / filter_text）。
        // 以下示範語法，實際使用時填入符合你資料的條件；未設定則不送出（NullValueHandling.Ignore）。
        // Dsl = "$privileges in [10, 20] and $containsAny in [1, 2, 3]",
        // DocumentIds = new List<string> { "doc_001_ai_intro" },
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
        // 這支端點不保證回 SSE：串流開始前就能判定的錯誤（聊天室不存在、輸入不合法、額度用完…）
        // 會回一般 JSON 錯誤信封。所以先看 status 與 Content-Type，確定是 SSE 才逐行解析。
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || mediaType != "text/event-stream")
        {
            await HandleErrorResponse(response);
            return (null, null);
        }

        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var fullMessage = new StringBuilder();
        string? line;
        int? returnedChatRoomId = null;
        int? latestChatLogId = null;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: "))
            {
                continue;
            }
            StreamChunk? chunk;
            try
            {
                chunk = JsonConvert.DeserializeObject<StreamChunk>(line.Substring(6));
            }
            catch (JsonException)
            {
                continue;
            }
            switch (chunk?.ChunkType)
            {
                case "thinking":
                    // 推理過程串流塊（GPT-5、Claude 等 reasoning 模型）
                    // 與最終回答(message)分開串流，這裡以灰底標示，不計入完整回應
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(chunk.Content);
                    Console.ResetColor();
                    Console.Out.Flush();
                    break;
                case "message":
                    Console.Write(chunk.Content);
                    Console.Out.Flush();
                    fullMessage.Append(chunk.Content);
                    break;
                case "chat_room":
                    var chatRoom = chunk.Data?.ToObject<ChatRoom>();
                    if (chatRoom != null)
                    {
                        returnedChatRoomId = chatRoom.Id;
                        latestChatLogId = chatRoom.LatestChatLogId;
                        Console.WriteLine("\n[聊天室資訊]");
                        ChatRoom.Print(chatRoom);
                    }
                    break;
                case "end":
                    // 被合規閘擋下時，data 另帶 blocked: true（與 detections，勿直接轉發給終端使用者）
                    var blocked = chunk.Data?["blocked"]?.Value<bool>() == true;
                    Console.WriteLine(blocked ? "\n[對話結束] 本輪被合規規則擋下" : "\n[對話結束]");
                    goto EndStream;
                case "error":
                    // error 與 end 只會出現其中一個；依 data.code 分流（見 README 錯誤代碼表），不要比對 content 文字
                    var code = chunk.Data?["code"]?.ToString();
                    var errorId = chunk.Data?["error_id"]?.ToString();
                    Console.WriteLine($"\n[錯誤] {chunk.Content} (代碼: {code ?? "無"}, error_id: {errorId ?? "無"})");
                    goto EndStream;
                default:
                    // status / agent_tool_call / agent_tool_result 等為過程資訊，純顯示用，可忽略
                    break;
            }
        }
        EndStream:
        Console.WriteLine($"\n完整回應長度: {fullMessage.Length} 字元");
        return (returnedChatRoomId, latestChatLogId);
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        return (null, null);
    }
}

// 取得聊天室列表（分頁：不帶 limit 只回前 100 筆，上限 1000；json_data 為聊天室陣列）
async Task<List<ChatRoom>?> GetChatRooms()
{
    Console.WriteLine("[聊天室列表]");
    var url = $"{baseUrl}/api/chat/chatrooms?limit=100&offset=0";
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
                    ChatRoom.Print(chatRoom);
                }
                return result.JsonData;
            }
            else
            {
                PrintApiError(result);
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

// 取得聊天記錄列表（分頁：不帶 limit 只回前 100 筆，上限 1000）
async Task<List<ChatLog>?> GetChatLogs(int chatRoomId)
{
    Console.WriteLine($"[聊天記錄查詢] (聊天室ID: {chatRoomId})");
    var url = $"{baseUrl}/api/chat/chatrooms/{chatRoomId}/chatlogs?limit=100&offset=0";
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
                    Console.WriteLine($"  使用者發問時間: {(log.HumanTime.HasValue ? log.HumanTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  AI 回答時間: {(log.AiTime.HasValue ? log.AiTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  建議問題: {JoinOrNone(log.SuggestQuestions)}");
                    DocumentResult.PrintList(log.SearchResults);
                    Console.WriteLine($"  語言: {log.Language}");
                    Console.WriteLine($"  查詢開始時間: {(log.QueryStartTime.HasValue ? log.QueryStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  查詢結束時間: {(log.QueryEndTime.HasValue ? log.QueryEndTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
                    Console.WriteLine($"  關鍵字: {JoinOrNone(log.Keywords)}");
                    Console.WriteLine($"  問題標準化: {(string.IsNullOrEmpty(log.Question) ? "無" : log.Question)}");
                    Console.WriteLine($"  評價: {log.RatingType ?? "無"}{(string.IsNullOrEmpty(log.RatingFeedback) ? "" : $"（{log.RatingFeedback}）")}");
                    Console.WriteLine();
                }
                return result.JsonData;
            }
            else
            {
                PrintApiError(result);
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
    var url = $"{baseUrl}/api/chat/chatlogs/{chatLogId}/rating";
    var ratingRequest = new RatingRequest
    {
        RatingType = "positive", // positive / negative
        Feedback = "這個回答很有幫助！" // 選填，上限 2000 字
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
            var result = JsonConvert.DeserializeObject<JsonResponse<JObject>>(responseContent);
            if (result?.Error == false)
            {
                Console.WriteLine($"✓ 評價提交成功: {result.Message}");
                Console.WriteLine($"  評價類型: {result.JsonData?["rating_type"]}");
                Console.WriteLine($"  評價回饋: {result.JsonData?["rating_feedback"]}");
                Console.WriteLine($"  評價時間: {result.JsonData?["rating_time"]}");
            }
            else
            {
                PrintApiError(result);
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

// 錯誤處理：所有非 SSE 的錯誤都回同一種 JSON 信封，分流請看 code（見 README 錯誤代碼表）
async Task HandleErrorResponse(HttpResponseMessage response)
{
    var errorContent = await response.Content.ReadAsStringAsync();
    try
    {
        var error = JsonConvert.DeserializeObject<JsonResponse<object>>(errorContent);
        if (error != null)
        {
            PrintApiError(error, (int)response.StatusCode);
            return;
        }
    }
    catch (JsonException) { }
    Console.WriteLine($"✗ HTTP 錯誤: {(int)response.StatusCode} {response.StatusCode}");
}

void PrintApiError<T>(JsonResponse<T>? error, int? httpStatus = null)
{
    if (error == null)
    {
        Console.WriteLine("✗ 回應格式無法解析");
        return;
    }
    // message 隨 Accept-Language 變化且可能帶 ": <detail>" 後綴，僅供顯示；判斷錯誤種類請用 code
    Console.WriteLine($"✗ API 錯誤: {error.Message} (代碼: {error.Code}, HTTP: {httpStatus ?? error.HttpStatus}"
        + (string.IsNullOrEmpty(error.ErrorId) ? ")" : $", error_id: {error.ErrorId})"));
}

// config 寫入時的模型能力檢查警告（無警告時為空陣列）
void PrintWarnings(List<JObject>? warnings)
{
    if (warnings == null || warnings.Count == 0)
    {
        return;
    }
    Console.WriteLine("  [警告]");
    foreach (var w in warnings)
    {
        Console.WriteLine($"    - {w["code"]} ({w["field"]}): {w["message"]}");
    }
}

static string JoinOrNone(IEnumerable<string>? items) =>
    items != null && items.Any() ? string.Join(", ", items) : "無";

// --- 資料結構定義 ---

public class ChatRequest
{
    [JsonProperty("chat_room_id")]
    public int? ChatRoomId { get; set; }
    [JsonProperty("chat_log_id")]
    public int? ChatLogId { get; set; }
    [JsonProperty("human_content")]
    public string HumanContent { get; set; } = "";
    [JsonProperty("config_name")]
    public string ConfigName { get; set; } = "default";
    [JsonProperty("user_id", NullValueHandling = NullValueHandling.Ignore)]
    public string? UserId { get; set; }
    [JsonProperty("selected_index", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? SelectedIndex { get; set; }
    // 篩選條件 DSL 字串（取代舊的 contains_any / privileges / filter_text 欄位）
    // 範例：$privileges in [10, 20] and $containsAny in [1, 2, 3]
    // 注意：請求採 extra=forbid，傳入已移除的舊欄位會直接回 422
    [JsonProperty("dsl", NullValueHandling = NullValueHandling.Ignore)]
    public string? Dsl { get; set; }
    // 限制搜尋的文件 ID 清單（選填）
    [JsonProperty("document_ids", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? DocumentIds { get; set; }
}

public class StreamChunk
{
    [JsonProperty("chunk_type")]
    public string? ChunkType { get; set; }
    [JsonProperty("content")]
    public string? Content { get; set; }
    [JsonProperty("data")]
    public JObject? Data { get; set; }
}

public class JsonResponse<T>
{
    [JsonProperty("json_data")]
    public T? JsonData { get; set; }
    [JsonProperty("error")]
    public bool Error { get; set; }
    [JsonProperty("message")]
    public string? Message { get; set; }
    [JsonProperty("code")]
    public int Code { get; set; }
    [JsonProperty("http_status")]
    public int HttpStatus { get; set; }
    // 系統錯誤才會帶，對應 chatbot 日誌中的那一行，回報問題時附上
    [JsonProperty("error_id")]
    public string? ErrorId { get; set; }
    // config 寫入端點才會帶（模型能力檢查警告）
    [JsonProperty("warnings")]
    public List<JObject>? Warnings { get; set; }
}

public class ChatRoom
{
    [JsonProperty("id")]
    public int Id { get; set; }
    [JsonProperty("title")]
    public string? Title { get; set; }
    [JsonProperty("description")]
    public string? Description { get; set; }
    [JsonProperty("role")]
    public string? Role { get; set; }
    [JsonProperty("product_system_prompt")]
    public string? ProductSystemPrompt { get; set; }
    [JsonProperty("status")]
    public string? Status { get; set; }
    [JsonProperty("user_id")]
    public string? UserId { get; set; }
    [JsonProperty("config_name")]
    public string? ConfigName { get; set; }
    [JsonProperty("config_version")]
    public int? ConfigVersion { get; set; }
    [JsonProperty("model_name")]
    public string? ModelName { get; set; }
    [JsonProperty("search_selected_number")]
    public int? SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number")]
    public int? SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio")]
    public float? DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode")]
    public string? UseKnowledgeMode { get; set; }
    [JsonProperty("enable_rerank")]
    public bool? EnableRerank { get; set; }
    [JsonProperty("memory_count")]
    public int? MemoryCount { get; set; }
    [JsonProperty("response_format")]
    public string? ResponseFormat { get; set; }
    [JsonProperty("enable_suggest_questions")]
    public bool? EnableSuggestQuestions { get; set; }
    // null = 不送 temperature 給模型
    [JsonProperty("temperature")]
    public float? Temperature { get; set; }
    [JsonProperty("document_field_mapping")]
    public Dictionary<string, string>? DocumentFieldMapping { get; set; }
    // 查詢失敗時為 null（代表「不知道」，不是 0）
    [JsonProperty("chat_logs_count")]
    public int? ChatLogsCount { get; set; }
    [JsonProperty("created_at")]
    public DateTime? CreatedAt { get; set; }
    [JsonProperty("updated_at")]
    public DateTime? UpdatedAt { get; set; }
    [JsonProperty("selected_index")]
    public List<string>? SelectedIndex { get; set; }
    [JsonProperty("dsl")]
    public string? Dsl { get; set; }
    [JsonProperty("document_ids")]
    public List<string>? DocumentIds { get; set; }

    // 以下欄位只在問答串流的 chat_room 塊出現
    [JsonProperty("latest_chat_log_id")]
    public int? LatestChatLogId { get; set; }
    [JsonProperty("suggest_questions")]
    public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")]
    public List<DocumentResult>? SearchResults { get; set; }
    // true = 本輪判定素材不足，走「查無答案」回覆
    [JsonProperty("grounding_no_answer")]
    public bool? GroundingNoAnswer { get; set; }
    // 本輪 token 用量 {input_tokens, output_tokens, total_tokens, model}；供應商未回報時為 null
    [JsonProperty("usage")]
    public JObject? Usage { get; set; }
    [JsonProperty("latency_ms")]
    public int? LatencyMs { get; set; }
    [JsonProperty("ttft_ms")]
    public int? TtftMs { get; set; }

    public static void Print(ChatRoom chatRoom)
    {
        Console.WriteLine($"  聊天室ID: {chatRoom.Id}");
        Console.WriteLine($"  聊天室標題: {chatRoom.Title}");
        Console.WriteLine($"  聊天室描述: {chatRoom.Description}");
        Console.WriteLine($"  使用者ID: {chatRoom.UserId ?? "無"}");
        Console.WriteLine($"  使用的配置: {chatRoom.ConfigName ?? "無"} (版本: {chatRoom.ConfigVersion?.ToString() ?? "無"})");
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
        Console.WriteLine($"  AI回應的創造性/隨機性: {chatRoom.Temperature?.ToString() ?? "未設定"}");
        Console.WriteLine($"  文件欄位映射關係: {(chatRoom.DocumentFieldMapping != null && chatRoom.DocumentFieldMapping.Count > 0 ? string.Join(", ", chatRoom.DocumentFieldMapping.Select(kv => $"{kv.Key}:{kv.Value}")) : "無")}");
        Console.WriteLine($"  聊天記錄數量: {chatRoom.ChatLogsCount?.ToString() ?? "—"}");
        Console.WriteLine($"  建立時間: {chatRoom.CreatedAt}");
        Console.WriteLine($"  更新時間: {chatRoom.UpdatedAt}");
        Console.WriteLine($"  選擇的文件索引列表: {(chatRoom.SelectedIndex != null && chatRoom.SelectedIndex.Count > 0 ? string.Join(", ", chatRoom.SelectedIndex) : "無")}");
        Console.WriteLine($"  篩選條件(dsl): {(string.IsNullOrEmpty(chatRoom.Dsl) ? "無" : chatRoom.Dsl)}");
        Console.WriteLine($"  限制搜尋的文件ID(document_ids): {(chatRoom.DocumentIds != null && chatRoom.DocumentIds.Count > 0 ? string.Join(", ", chatRoom.DocumentIds) : "無")}");
        if (chatRoom.LatestChatLogId.HasValue)
        {
            Console.WriteLine($"  最新聊天記錄ID: {chatRoom.LatestChatLogId}");
            Console.WriteLine($"  建議問題: {(chatRoom.SuggestQuestions != null && chatRoom.SuggestQuestions.Count > 0 ? string.Join(", ", chatRoom.SuggestQuestions) : "無")}");
            Console.WriteLine($"  查無答案(grounding_no_answer): {(chatRoom.GroundingNoAnswer == true ? "是" : "否")}");
            Console.WriteLine($"  token 用量: {(chatRoom.Usage != null ? $"輸入 {chatRoom.Usage["input_tokens"]} / 輸出 {chatRoom.Usage["output_tokens"]} / 合計 {chatRoom.Usage["total_tokens"]}" : "無")}");
            Console.WriteLine($"  耗時: {chatRoom.LatencyMs} ms（首個 token: {chatRoom.TtftMs} ms）");
            DocumentResult.PrintList(chatRoom.SearchResults);
        }
    }
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
    public string? HumanContent { get; set; }
    [JsonProperty("ai_content")]
    public string? AiContent { get; set; }
    [JsonProperty("human_time")]
    public DateTime? HumanTime { get; set; }
    [JsonProperty("ai_time")]
    public DateTime? AiTime { get; set; }
    [JsonProperty("suggest_questions")]
    public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")]
    public List<DocumentResult>? SearchResults { get; set; }
    [JsonProperty("language")]
    public string? Language { get; set; }
    [JsonProperty("query_start_time")]
    public DateTime? QueryStartTime { get; set; }
    [JsonProperty("query_end_time")]
    public DateTime? QueryEndTime { get; set; }
    [JsonProperty("keywords")]
    public List<string>? Keywords { get; set; }
    [JsonProperty("question")]
    public string? Question { get; set; }
    [JsonProperty("rating_type")]
    public string? RatingType { get; set; }
    [JsonProperty("rating_feedback")]
    public string? RatingFeedback { get; set; }
    [JsonProperty("rating_time")]
    public DateTime? RatingTime { get; set; }
}

public class RatingRequest
{
    [JsonProperty("rating_type")]
    public string RatingType { get; set; } = "positive";
    [JsonProperty("feedback", NullValueHandling = NullValueHandling.Ignore)]
    public string? Feedback { get; set; }
}

// 建立 / 更新 config 的請求：寫入為局部更新，null 欄位不送出（沿用預設值或原值）
public class ConfigRequest
{
    [JsonProperty("product_system_prompt", NullValueHandling = NullValueHandling.Ignore)]
    public string? ProductSystemPrompt { get; set; }
    [JsonProperty("role", NullValueHandling = NullValueHandling.Ignore)]
    public string? Role { get; set; }
    [JsonProperty("model_name", NullValueHandling = NullValueHandling.Ignore)]
    public string? ModelName { get; set; }
    [JsonProperty("search_selected_number", NullValueHandling = NullValueHandling.Ignore)]
    public int? SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number", NullValueHandling = NullValueHandling.Ignore)]
    public int? SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio", NullValueHandling = NullValueHandling.Ignore)]
    public float? DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode", NullValueHandling = NullValueHandling.Ignore)]
    public string? UseKnowledgeMode { get; set; }
    [JsonProperty("enable_rerank", NullValueHandling = NullValueHandling.Ignore)]
    public bool? EnableRerank { get; set; }
    [JsonProperty("reranker_name", NullValueHandling = NullValueHandling.Ignore)]
    public string? RerankerName { get; set; }
    [JsonProperty("memory_count", NullValueHandling = NullValueHandling.Ignore)]
    public int? MemoryCount { get; set; }
    [JsonProperty("enable_suggest_questions", NullValueHandling = NullValueHandling.Ignore)]
    public bool? EnableSuggestQuestions { get; set; }
    [JsonProperty("response_format", NullValueHandling = NullValueHandling.Ignore)]
    public string? ResponseFormat { get; set; }
    [JsonProperty("document_field_mapping", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string>? DocumentFieldMapping { get; set; }
    [JsonProperty("selected_index", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? SelectedIndex { get; set; }
}

public class DocumentResult
{
    [JsonProperty("doc_name")]
    public string? DocName { get; set; }

    [JsonProperty("document_id")]
    public string? DocumentId { get; set; }

    [JsonProperty("chunk_index")]
    public int? ChunkIndex { get; set; }

    [JsonProperty("data_source")]
    public string? DataSource { get; set; }

    [JsonProperty("index")]
    public string? Index { get; set; }

    [JsonProperty("search_mode")]
    public string? SearchMode { get; set; }

    // 上游沒給日期或無法解析時為 null
    [JsonProperty("last_modified")]
    public DateTime? LastModified { get; set; }

    [JsonProperty("score")]
    public float? Score { get; set; }

    // 回答中的 [[N]] 即指 source_no == N 的來源；null = 未提供給模型
    [JsonProperty("source_no")]
    public int? SourceNo { get; set; }

    [JsonProperty("document")]
    public Dictionary<string, object>? Document { get; set; }

    public static void PrintList(List<DocumentResult>? results)
    {
        if (results == null || results.Count == 0)
        {
            Console.WriteLine("  搜尋結果: 無");
            return;
        }
        Console.WriteLine("  搜尋結果:");
        int idx = 1;
        foreach (var r in results)
        {
            Console.WriteLine($"    - 結果 {idx++}:");
            Console.WriteLine($"      來源編號(source_no): {r.SourceNo?.ToString() ?? "無"}");
            Console.WriteLine($"      文件名稱(doc_name): {r.DocName}");
            Console.WriteLine($"      文件ID(document_id): {r.DocumentId}");
            Console.WriteLine($"      區塊索引(chunk_index): {r.ChunkIndex}");
            Console.WriteLine($"      資料來源(data_source): {r.DataSource}");
            Console.WriteLine($"      索引名稱(index): {r.Index}");
            Console.WriteLine($"      搜索模式(search_mode): {r.SearchMode}");
            Console.WriteLine($"      最後修改時間(last_modified): {(r.LastModified.HasValue ? r.LastModified.Value.ToString("yyyy-MM-dd HH:mm:ss") : "無")}");
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

public class ChatConfig
{
    [JsonProperty("config_name")]
    public string? ConfigName { get; set; } // 配置名稱（列表才有）
    [JsonProperty("version_no")]
    public int? VersionNo { get; set; } // 目前版本號（列表才有）
    [JsonProperty("chatroom_system_prompt")]
    public string? ChatroomSystemPrompt { get; set; } // 聊天室系統提示詞
    [JsonProperty("product_system_prompt")]
    public string? ProductSystemPrompt { get; set; } // 產品系統提示詞
    [JsonProperty("intension_system_prompt")]
    public string? IntensionSystemPrompt { get; set; } // 意圖系統提示詞
    [JsonProperty("role")]
    public string? Role { get; set; } // 機器人身份設定
    [JsonProperty("model_name")]
    public string? ModelName { get; set; } // 使用的AI模型名稱
    [JsonProperty("search_selected_number")]
    public int? SearchSelectedNumber { get; set; } // 實際用於上下文的搜索結果數量
    [JsonProperty("search_total_number")]
    public int? SearchTotalNumber { get; set; } // 檢索的總搜索結果數量
    [JsonProperty("data_source_ratio")]
    public float? DataSourceRatio { get; set; } // 詞向量搜索比例 (0.0=純向量, 1.0=純關鍵字)
    [JsonProperty("use_knowledge_mode")]
    public string? UseKnowledgeMode { get; set; } // 知識使用模式：none/assist/strict
    [JsonProperty("enable_rerank")]
    public bool? EnableRerank { get; set; } // 是否重新排序搜索結果
    [JsonProperty("memory_count")]
    public int? MemoryCount { get; set; } // 記住的歷史對話數量
    [JsonProperty("enable_suggest_questions")]
    public bool? EnableSuggestQuestions { get; set; } // 是否啟用推薦問題
    [JsonProperty("response_format")]
    public string? ResponseFormat { get; set; } // 回應格式：markdown/html
    [JsonProperty("temperature")]
    public float? Temperature { get; set; } // AI回應的創造性/隨機性（null = 不送給模型）
    [JsonProperty("timezone")]
    public string? Timezone { get; set; } // 時區
    [JsonProperty("document_field_mapping")]
    public Dictionary<string, string>? DocumentFieldMapping { get; set; } // 文件欄位映射表
    [JsonProperty("selected_index")]
    public List<string>? SelectedIndex { get; set; } // 選擇的文件索引列表

    public static void Print(ChatConfig config)
    {
        Console.WriteLine("【配置資訊】");
        if (config.ConfigName != null)
        {
            Console.WriteLine($"  配置名稱: {config.ConfigName} (版本: {config.VersionNo?.ToString() ?? "無"})");
        }
        Console.WriteLine($"  聊天室系統提示詞: {config.ChatroomSystemPrompt ?? "(無)"}");
        Console.WriteLine($"  產品系統提示詞: {config.ProductSystemPrompt ?? "(無)"}");
        Console.WriteLine($"  意圖系統提示詞: {config.IntensionSystemPrompt ?? "(無)"}");
        Console.WriteLine($"  機器人身份設定: {config.Role}");
        Console.WriteLine($"  使用的AI模型名稱: {config.ModelName}");
        Console.WriteLine($"  實際用於上下文的搜索結果數量: {config.SearchSelectedNumber}");
        Console.WriteLine($"  檢索的總搜索結果數量: {config.SearchTotalNumber}");
        Console.WriteLine($"  詞向量搜索比例: {config.DataSourceRatio}");
        Console.WriteLine($"  知識使用模式: {config.UseKnowledgeMode}");
        Console.WriteLine($"  是否重新排序搜索結果: {(config.EnableRerank == true ? "是" : "否")}");
        Console.WriteLine($"  記住的歷史對話數量: {config.MemoryCount}");
        Console.WriteLine($"  是否啟用推薦問題: {(config.EnableSuggestQuestions == true ? "是" : "否")}");
        Console.WriteLine($"  回應格式: {config.ResponseFormat}");
        Console.WriteLine($"  AI回應的創造性/隨機性: {config.Temperature?.ToString() ?? "未設定"}");
        Console.WriteLine($"  時區: {config.Timezone ?? "(無)"}");
        Console.WriteLine($"  文件欄位映射表: {(config.DocumentFieldMapping != null && config.DocumentFieldMapping.Count > 0 ? string.Join(", ", config.DocumentFieldMapping.Select(kv => $"{kv.Key}:{kv.Value}")) : "無")}");
        Console.WriteLine($"  選擇的文件索引列表: {(config.SelectedIndex != null && config.SelectedIndex.Count > 0 ? string.Join(", ", config.SelectedIndex) : "無")}");
    }
}
