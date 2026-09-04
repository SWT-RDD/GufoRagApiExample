using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// ============================================================================
// GufoRAG Chat API 全流程範例（給產品後端整合者）
//
// 流程：模型目錄 → 建立設定 → 讀回改寫（局部更新）→ 版本歷史 → 設定清單
//       → 第一輪問答（新建聊天室）→ 續談 → 聊天室清單 → 單室紀錄 → 評價 → 單筆紀錄
//       → 全域紀錄過濾 → 執行軌跡 → 使用次數 → 刪除示範用設定
//
// 幾個貫穿全程的規則（詳見 README「整合前必讀的八件事」）：
//   - 回應一律是 {json_data, error, message, code, http_status} 信封；分流看 code 不看 message。
//   - 未知欄位／未宣告的 query 參數一律 422。
//   - 清單端點一律回一頁（預設 100 筆），要全部就自己翻頁。
//   - GET 設定回應多一個 version_no，原樣 PUT 回去會 422；PUT 是局部更新。
//   - 聊天端點不保證回 SSE：先看狀態碼與 Content-Type 再掛串流解析器。
// ============================================================================

var handler = new HttpClientHandler();
handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; // 自簽憑證環境用；正式環境請拿掉
var client = new HttpClient(handler);
client.DefaultRequestHeaders.Add("Accept-Language", "zh-TW");

string baseUrl = "http://localhost:5555";
string configName = "demo_config";
string userId = "demo_user";
string tenantId = "demo_tenant";

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== GufoRAG Chat API 全流程範例 ===\n");

try
{
    // ── 1. 模型目錄：設定頁的模型下拉、思考深度與 temperature 旋鈕的可用性都從這裡來 ──
    Console.WriteLine("[1] GET /api/config/models");
    var models = await GetJson<ModelCatalog>($"{baseUrl}/api/config/models");
    if (models?.Models != null)
    {
        Console.WriteLine($"  共 {models.Models.Count} 顆模型，列前 5 顆：");
        foreach (var m in models.Models.Take(5))
        {
            var effort = m.ReasoningOptions.Count > 0 ? string.Join("/", m.ReasoningOptions) : "（無思考深度旋鈕）";
            Console.WriteLine($"  - {m.Value}  provider={m.Provider}  reasoning={effort}  omit_temperature={m.OmitTemperature}");
        }
    }
    Console.WriteLine();

    // ── 2. 建立設定：只送要設的欄位，其餘落預設；回應是「你送了什麼」，要整份請再 GET ──
    Console.WriteLine($"[2] POST /api/config/{configName}");
    var newConfig = new ConfigRequest
    {
        ProductSystemPrompt = "你是測試用助手，回答要簡短。",
        Role = "測試助手",
        ModelName = "openai:gpt-5-mini",
        SearchSelectedNumber = 3,
        SearchTotalNumber = 6,
        DataSourceRatio = 0.0f,
        UseKnowledgeMode = "strict",
        EnableRerank = false,
        MemoryCount = 5,
        EnableSuggestQuestions = true,
        ResponseFormat = "markdown",
        SelectedIndex = new List<string> { "faq_docs" },
    };
    var created = await PostJson<JObject>($"{baseUrl}/api/config/{configName}", newConfig);
    if (created.Ok)
    {
        Console.WriteLine($"  建立成功，送出的欄位：{string.Join(", ", created.Data!.Properties().Select(p => p.Name))}");
    }
    else if (created.Code == 1005)
    {
        Console.WriteLine("  設定已存在（或名稱是保留字），沿用既有的繼續往下走。");
    }
    Console.WriteLine();

    // ── 3. 讀回、改一欄、局部更新。GET 回應帶 version_no，PUT 前要剝掉 ──
    Console.WriteLine($"[3] GET /api/config/{configName} → PUT（局部更新）");
    var loaded = await GetJson<JObject>($"{baseUrl}/api/config/{configName}");
    if (loaded != null)
    {
        Console.WriteLine($"  目前 version_no={loaded["version_no"]}，temperature={loaded["temperature"]}，欄位數={loaded.Count}");

        // 只送要改的欄位。這裡示範「GET 改 PUT」時必須剝掉 version_no：
        // 它不是設定欄位，寫入模型禁止未知鍵，原樣送回去會 422。
        var patch = new JObject
        {
            ["temperature"] = 0.2,
            ["role"] = "測試助手（第二版）",
        };
        var updated = await PutJson<JObject>($"{baseUrl}/api/config/{configName}?source=params_edit", patch);
        Console.WriteLine(updated.Ok ? $"  更新成功：{updated.Data}" : "  更新失敗（見上方錯誤）");
    }
    Console.WriteLine();

    // ── 4. 版本歷史：每次寫入 append 一版；?changed= 可只看某欄有變動的版本 ──
    Console.WriteLine($"[4] GET /api/config/{configName}/versions");
    var versions = await GetJson<ConfigVersions>($"{baseUrl}/api/config/{configName}/versions");
    if (versions?.Versions != null)
    {
        foreach (var v in versions.Versions)
        {
            Console.WriteLine($"  v{v.VersionNo}  source={v.Source}  changed=[{string.Join(", ", v.ChangedFields)}]  current={v.IsCurrent}");
        }
    }
    Console.WriteLine();

    // ── 5. 設定清單：裸清單、沒有 total_count、不帶 limit 只回 100 筆 ──
    Console.WriteLine("[5] GET /api/config/list?limit=1000");
    var configList = await GetJson<List<JObject>>($"{baseUrl}/api/config/list?limit=1000&offset=0");
    if (configList != null)
    {
        Console.WriteLine($"  共 {configList.Count} 份設定：{string.Join(", ", configList.Select(c => c["config_name"]))}");
    }
    Console.WriteLine();

    // ── 6. 第一輪問答（chat_room_id=null ⇒ 新建聊天室）──
    Console.WriteLine("[6] POST /api/chat/chatbot（新建聊天室）");
    var firstTurn = new ChatRequest
    {
        HumanContent = "請問什麼是人工智慧？",
        ConfigName = "default",
        UserId = userId,
        TenantId = tenantId,
        Tag = "demo",
        // 篩選條件與文件限縮依你的資料填；沒設就不送（NullValueHandling.Ignore）。
        // Dsl = "$privileges in [10, 20] and $containsAny in [1, 2, 3]",
        // DocumentIds = new List<string> { "doc_001_ai_intro" },
    };
    var (chatRoomId, latestChatLogId) = await ChatWithBot(firstTurn);
    Console.WriteLine();

    if (chatRoomId is null)
    {
        Console.WriteLine("第一輪沒有拿到聊天室 ID，後續步驟跳過。");
        return;
    }

    // ── 7. 續談：帶 chat_room_id。設定用的是房間建立時凍結的那一份，config_name 被忽略 ──
    Console.WriteLine("[7] POST /api/chat/chatbot（續談）");
    var secondTurn = new ChatRequest
    {
        ChatRoomId = chatRoomId,
        HumanContent = "那它和機器學習有什麼差別？",
        UserId = userId,
    };
    var (_, secondLogId) = await ChatWithBot(secondTurn);
    latestChatLogId = secondLogId ?? latestChatLogId;
    Console.WriteLine();

    // ── 8. 聊天室清單：兩支端點形狀不同（裸清單 vs 帶 total_count 的信封）──
    Console.WriteLine("[8] GET /api/chat/chatrooms?limit=5 與 /chatrooms/user/{user_id}?limit=5");
    var rooms = await GetJson<List<ChatRoom>>($"{baseUrl}/api/chat/chatrooms?limit=5&offset=0");
    if (rooms != null)
    {
        Console.WriteLine($"  全域清單（本頁 {rooms.Count} 間）：");
        foreach (var r in rooms) PrintRoomSummary(r);
    }
    var userRooms = await GetJson<UserChatRoomsPage>($"{baseUrl}/api/chat/chatrooms/user/{userId}?limit=5&offset=0");
    if (userRooms != null)
    {
        Console.WriteLine($"  使用者 {userRooms.UserId} 共 {userRooms.TotalCount} 間，本頁 {userRooms.ReturnedCount} 間");
    }
    Console.WriteLine();

    // ── 9. 單室紀錄：裸清單、分頁；preview_chars 只裁長文欄位 ──
    Console.WriteLine($"[9] GET /api/chat/chatrooms/{chatRoomId}/chatlogs?limit=100&preview_chars=300");
    var roomLogs = await GetJson<List<ChatLog>>($"{baseUrl}/api/chat/chatrooms/{chatRoomId}/chatlogs?limit=100&offset=0&preview_chars=300");
    if (roomLogs != null)
    {
        Console.WriteLine($"  共 {roomLogs.Count} 則（本頁）");
        foreach (var log in roomLogs) PrintChatLog(log);
    }
    Console.WriteLine();

    // ── 10. 評價 ──
    if (latestChatLogId is int logId)
    {
        Console.WriteLine($"[10] POST /api/chat/chatlogs/{logId}/rating");
        var rating = await PostJson<RatingResponse>($"{baseUrl}/api/chat/chatlogs/{logId}/rating",
            new RatingRequest { RatingType = "positive", Feedback = "這個回答很有幫助！" });
        if (rating.Ok)
        {
            Console.WriteLine($"  評價成功：{rating.Data!.RatingType} @ {rating.Data.RatingTime}（tag={rating.Data.Tag}, prompt_version={rating.Data.PromptVersion}）");
        }
        Console.WriteLine();

        // ── 11. 單筆紀錄：比清單多回稽核脈絡（system_prompt 是提示詞原文，不要轉發給終端使用者）──
        Console.WriteLine($"[11] GET /api/chat/chatlogs/{logId}");
        var one = await GetJson<JObject>($"{baseUrl}/api/chat/chatlogs/{logId}");
        if (one != null)
        {
            Console.WriteLine($"  rating_type={one["rating_type"]}  answer_source={one["answer_source"] ?? "null（一般生成）"}  search_mode={one["search_mode"]}");
            Console.WriteLine($"  chat_room_title={one["chat_room_title"]}  model_name={one["model_name"]}  total_tokens={one["total_tokens"]}");
        }
        Console.WriteLine();

        // ── 12. 執行軌跡：完整步驟事件與工具軌跡，後台流程圖重播用 ──
        Console.WriteLine($"[12] GET /api/chat/chatlogs/{logId}/trace");
        var trace = await GetJson<JObject>($"{baseUrl}/api/chat/chatlogs/{logId}/trace");
        if (trace != null)
        {
            var steps = trace["step_events"] as JArray;
            Console.WriteLine($"  step_events={steps?.Count ?? 0} 筆  tool_activity={(trace["tool_activity"] as JArray)?.Count ?? 0} 筆");
            if (steps != null)
            {
                foreach (var s in steps.Take(8))
                {
                    Console.WriteLine($"    - {s["step_id"]}  {s["step_status"]}  {s["duration_ms"]?.ToString() ?? "-"} ms");
                }
            }
        }
        Console.WriteLine();
    }

    // ── 13. 全域紀錄過濾：信封含 total_count 與 filters_applied（回聲實際套用的值）──
    Console.WriteLine($"[13] GET /api/chat/chatlogs?user_id={userId}&limit=5");
    var page = await GetJson<ChatLogsPage>($"{baseUrl}/api/chat/chatlogs?user_id={Uri.EscapeDataString(userId)}&limit=5&offset=0&preview_chars=200");
    if (page != null)
    {
        Console.WriteLine($"  total_count={page.TotalCount}  returned_count={page.ReturnedCount}  filters_applied={page.FiltersApplied?.ToString(Formatting.None)}");
    }
    Console.WriteLine();

    // ── 14. 使用次數：判斷還能不能問請看 is_allowed，不要對 remaining_usage 做算術 ──
    Console.WriteLine("[14] GET /api/chat/usage-status");
    var usage = await GetJson<UsageStatus>($"{baseUrl}/api/chat/usage-status");
    if (usage != null)
    {
        Console.WriteLine($"  is_allowed={usage.IsAllowed}  is_unlimited={usage.IsUnlimited}  current={usage.CurrentUsage}  max={usage.MaxUsage}  remaining={usage.RemainingUsage}");
    }
    Console.WriteLine();

    // ── 15. 刪除示範用設定。default 不可刪；被聊天室引用的設定照刪，既有房間靠凍結值繼續運作 ──
    Console.WriteLine($"[15] DELETE /api/config/{configName}");
    var deleted = await DeleteJson<JObject>($"{baseUrl}/api/config/{configName}");
    if (deleted.Ok) Console.WriteLine($"  已刪除：{deleted.Data!["deleted_config"]}");
}
catch (Exception ex)
{
    Console.WriteLine($"錯誤: {ex.Message}");
}

// ============================================================================
// 聊天對話（SSE 串流）
// ============================================================================
async Task<(int? chatRoomId, int? latestChatLogId)> ChatWithBot(ChatRequest request)
{
    Console.WriteLine($"  使用者輸入: {request.HumanContent}");
    var json = JsonConvert.SerializeObject(request);
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat/chatbot")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    // ResponseHeadersRead：標頭到就開始讀，不等整個 body。
    using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

    // 這支端點不保證回 SSE：404／400／422／429／503 會在串流開始之前回普通 JSON 信封。
    var mediaType = response.Content.Headers.ContentType?.MediaType;
    if (!response.IsSuccessStatusCode || mediaType != "text/event-stream")
    {
        await HandleErrorResponse(response);
        return (null, null);
    }

    using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream, Encoding.UTF8);

    var answer = new StringBuilder();
    int? roomId = null;
    int? latestLogId = null;
    bool inThinking = false;

    Console.Write("  AI 回應: ");
    while (!reader.EndOfStream)
    {
        var line = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

        JObject chunk;
        try { chunk = JObject.Parse(line.Substring(6)); }
        catch (JsonException) { continue; }

        var type = chunk["chunk_type"]?.ToString();
        var content = chunk["content"]?.ToString() ?? "";
        var data = chunk["data"] as JObject;

        switch (type)
        {
            case "thinking":
                // 推理內容與答案分開顯示，不併進答案。
                if (!inThinking) { Console.ForegroundColor = ConsoleColor.DarkGray; inThinking = true; }
                Console.Write(content);
                break;

            case "message":
                if (inThinking) { Console.ResetColor(); inThinking = false; Console.WriteLine(); Console.Write("  AI 回應: "); }
                Console.Write(content);
                answer.Append(content);
                break;

            case "status":
                // 分流比 data.status_code，不比 content（content 隨 Accept-Language 變）。
                // 有 step_id 的是步驟事件（進時間軸），沒有的是旁白。
                if (data?["step_id"] != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write($"\n  [步驟 {data["step_id"]} {data["step_status"]}{(data["duration_ms"] != null ? $" {data["duration_ms"]}ms" : "")}]");
                    if (data["verdict"] != null) Console.Write($" verdict={data["verdict"]}");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.Write("  ");
                }
                break;

            case "agent_tool_call":
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"\n  [工具呼叫 {data?["tool"]} args={data?["args"]?.ToString(Formatting.None)}]\n  ");
                Console.ResetColor();
                break;

            case "agent_tool_result":
                Console.ForegroundColor = ConsoleColor.Yellow;
                var preview = data?["result_preview"]?.ToString() ?? "";
                Console.Write($"\n  [工具結果 {data?["tool"]} source_index={data?["source_index"]}] {(preview.Length > 80 ? preview[..80] + "…" : preview)}\n  ");
                Console.ResetColor();
                break;

            case "chat_room":
                if (inThinking) { Console.ResetColor(); inThinking = false; }
                if (data != null)
                {
                    var room = data.ToObject<ChatRoom>();
                    if (room != null)
                    {
                        roomId = room.Id;
                        latestLogId = room.LatestChatLogId;
                        Console.WriteLine("\n\n  [聊天室資訊]");
                        Console.WriteLine($"    聊天室ID: {room.Id}   最新紀錄ID: {room.LatestChatLogId}   標題: {room.Title}");
                        Console.WriteLine($"    設定: {room.ConfigName} v{room.ConfigVersion}   模型: {room.ModelName}   search_mode: {room.SearchMode}");
                        Console.WriteLine($"    使用者: {room.UserId}   租戶: {room.TenantId}   標籤: {room.Tag}");
                        Console.WriteLine($"    誠實查無: {room.GroundingNoAnswer}   意圖命中索引: {room.IntentIndex ?? "無"}   對話則數: {room.ChatLogsCount?.ToString() ?? "未知"}");
                        Console.WriteLine($"    latency_ms: {room.LatencyMs}   ttft_ms: {room.TtftMs}   answer_source: {(room.AnswerSource ?? "一般生成")}");
                        if (room.Usage != null)
                        {
                            // usage 的鍵集是開放的：供應商有回報快取明細才有 cache_* 鍵，用索引取、缺席不等於 0。
                            Console.WriteLine($"    用量: input={room.Usage["input_tokens"]} output={room.Usage["output_tokens"]} total={room.Usage["total_tokens"]} model={room.Usage["model"]}");
                        }
                        else
                        {
                            Console.WriteLine("    用量: 供應商未回報");
                        }
                        Console.WriteLine($"    推薦問題: {(room.SuggestQuestions is { Count: > 0 } ? string.Join(" | ", room.SuggestQuestions) : "無")}");
                        if (room.SearchResults is { Count: > 0 })
                        {
                            Console.WriteLine($"    來源卡 {room.SearchResults.Count} 張：");
                            foreach (var card in room.SearchResults)
                            {
                                // source_no 為 null 代表模型沒看過這張卡，答案裡不會有 [[N]] 指到它。
                                var no = card.SourceNo.HasValue ? $"[[{card.SourceNo}]]" : "[未進上下文]";
                                var modified = card.LastModified.HasValue ? card.LastModified.Value.ToString("yyyy-MM-dd") : "未提供";
                                Console.WriteLine($"      {no} {card.DocName} (index={card.Index}, score={card.Score:F3}, last_modified={modified})");
                            }
                        }
                    }
                }
                break;

            case "error":
                // 終止塊：顯示訊息並收線，之後不會有 end。
                if (inThinking) { Console.ResetColor(); inThinking = false; }
                Console.WriteLine($"\n  [錯誤] {content}");
                goto EndStream;

            case "end":
                if (inThinking) { Console.ResetColor(); inThinking = false; }
                // 被合規閘擋下時 data 多 blocked 與 detections；輸入閘擋下且已建房時另有 chat_room_id（此時沒有 chat_room 塊）。
                if (data?["blocked"]?.Value<bool>() == true)
                {
                    Console.WriteLine($"\n  [對話結束：被合規閘擋下] detections={data["detections"]?.ToString(Formatting.None)}");
                    if (roomId is null && data["chat_room_id"] != null) roomId = data["chat_room_id"]!.Value<int>();
                }
                else
                {
                    Console.WriteLine("\n  [對話結束]");
                }
                goto EndStream;
        }
    }
EndStream:
    Console.ResetColor();
    Console.WriteLine($"  完整回應長度: {answer.Length} 字元");
    return (roomId, latestLogId);
}

// ============================================================================
// HTTP 輔助方法：全部走同一個信封
// ============================================================================
async Task<T?> GetJson<T>(string url) where T : class
{
    var response = await client.GetAsync(url);
    var envelope = await ReadEnvelope<T>(response);
    return envelope.Ok ? envelope.Data : null;
}

async Task<ApiResult<T>> PostJson<T>(string url, object body) where T : class
{
    var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
    var response = await client.PostAsync(url, content);
    return await ReadEnvelope<T>(response);
}

async Task<ApiResult<T>> PutJson<T>(string url, object body) where T : class
{
    var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
    var response = await client.PutAsync(url, content);
    return await ReadEnvelope<T>(response);
}

async Task<ApiResult<T>> DeleteJson<T>(string url) where T : class
{
    var response = await client.DeleteAsync(url);
    return await ReadEnvelope<T>(response);
}

async Task<ApiResult<T>> ReadEnvelope<T>(HttpResponseMessage response) where T : class
{
    var text = await response.Content.ReadAsStringAsync();
    JsonResponse<T>? envelope = null;
    try { envelope = JsonConvert.DeserializeObject<JsonResponse<T>>(text); }
    catch (JsonException) { }

    if (envelope == null)
    {
        Console.WriteLine($"  ✗ HTTP {(int)response.StatusCode}，回應不是信封：{(text.Length > 200 ? text[..200] : text)}");
        return new ApiResult<T> { Ok = false, HttpStatus = (int)response.StatusCode };
    }
    if (envelope.Error || !response.IsSuccessStatusCode)
    {
        PrintApiError(envelope, (int)response.StatusCode);
        return new ApiResult<T> { Ok = false, Code = envelope.Code, HttpStatus = envelope.HttpStatus, Message = envelope.Message };
    }
    return new ApiResult<T> { Ok = true, Data = envelope.JsonData, Code = envelope.Code, HttpStatus = envelope.HttpStatus, Message = envelope.Message };
}

async Task HandleErrorResponse(HttpResponseMessage response)
{
    var text = await response.Content.ReadAsStringAsync();
    try
    {
        var envelope = JsonConvert.DeserializeObject<JsonResponse<object>>(text);
        if (envelope != null) { PrintApiError(envelope, (int)response.StatusCode); return; }
    }
    catch (JsonException) { }
    Console.WriteLine($"  ✗ HTTP {(int)response.StatusCode}：{text}");
}

void PrintApiError<T>(JsonResponse<T> envelope, int httpStatus)
{
    // 分流看 code。幾個要特別處理的狀態：
    //   422 形狀錯（改請求的形狀）、400 值錯、404 給錯 id、429 今日額度用完（可重試）、503 上游額度服務不可用。
    var hint = httpStatus switch
    {
        422 => "請求形狀不符（未知欄位、型別錯、超過上界或未宣告的 query 參數）",
        429 => "每日額度用完，明天再試",
        503 => "上游服務暫時不可用，稍後重試",
        404 => "資源不存在（id 給錯，不是服務故障）",
        _ => "",
    };
    Console.WriteLine($"  ✗ API 錯誤 code={envelope.Code} http={httpStatus}{(envelope.Field != null ? $" field={envelope.Field}" : "")}：{envelope.Message}{(hint != "" ? $"（{hint}）" : "")}");
}

void PrintRoomSummary(ChatRoom r)
{
    Console.WriteLine($"    #{r.Id} {r.Title}  config={r.ConfigName} v{r.ConfigVersion}  user={r.UserId}  logs={r.ChatLogsCount}  updated={r.UpdatedAt:yyyy-MM-dd HH:mm}");
}

void PrintChatLog(ChatLog log)
{
    Console.WriteLine("    ------------------------------");
    Console.WriteLine($"    #{log.Id}  prev={log.PreviousChatLogId?.ToString() ?? "無"}  {log.HumanTime:yyyy-MM-dd HH:mm:ss}  tag={log.Tag ?? "無"}");
    Console.WriteLine($"    Q: {log.HumanContent}");
    Console.WriteLine($"    A: {(log.AiContent is { Length: > 120 } a ? a[..120] + "…" : log.AiContent)}");
    Console.WriteLine($"    language={log.Language ?? "null（本輪未偵測）"}  search_mode={log.SearchMode}  answer_source={log.AnswerSource ?? "null（一般生成）"}  blocked_by={log.BlockedBy ?? "無"}");
    Console.WriteLine($"    tokens in/out/total={log.InputTokens?.ToString() ?? "-"}/{log.OutputTokens?.ToString() ?? "-"}/{log.TotalTokens?.ToString() ?? "-"}  ttft_ms={log.TtftMs?.ToString() ?? "-"}  rating={log.RatingType ?? "未評價"}");
    Console.WriteLine($"    來源卡: {log.SearchResults?.Count ?? 0} 張  推薦問題: {(log.SuggestQuestions is { Count: > 0 } ? string.Join(" | ", log.SuggestQuestions) : "無")}");
}

// ============================================================================
// 資料結構
// ============================================================================

/// <summary>所有端點共用的回應信封。</summary>
public class JsonResponse<T>
{
    [JsonProperty("json_data")] public T? JsonData { get; set; }
    [JsonProperty("error")] public bool Error { get; set; }
    [JsonProperty("message")] public string Message { get; set; } = "";
    [JsonProperty("code")] public int Code { get; set; }
    [JsonProperty("http_status")] public int HttpStatus { get; set; }
    /// <summary>可選鍵：指得出是哪一個輸入欄出錯時才有（目前只有 MCP 寫入面的 env／args）。</summary>
    [JsonProperty("field")] public string? Field { get; set; }
}

public class ApiResult<T>
{
    public bool Ok { get; set; }
    public T? Data { get; set; }
    public int Code { get; set; }
    public int HttpStatus { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>POST /api/chat/chatbot 請求體。未知欄位一律 422，所以這裡只列服務宣告的欄位。</summary>
public class ChatRequest
{
    [JsonProperty("chat_room_id")] public int? ChatRoomId { get; set; }      // null＝新建聊天室；0 與負數會 422
    [JsonProperty("chat_log_id")] public int? ChatLogId { get; set; }        // 在某一則之後續談（分支）
    [JsonProperty("human_content")] public string HumanContent { get; set; } = "";   // 業務上限 strip 後 1–2000 字
    [JsonProperty("config_name")] public string ConfigName { get; set; } = "default"; // 續談時以房間凍結的為準
    [JsonProperty("user_id", NullValueHandling = NullValueHandling.Ignore)] public string? UserId { get; set; }
    [JsonProperty("tenant_id", NullValueHandling = NullValueHandling.Ignore)] public string? TenantId { get; set; }
    [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)] public string? Tag { get; set; }
    [JsonProperty("prompt_version", NullValueHandling = NullValueHandling.Ignore)] public int? PromptVersion { get; set; }
    [JsonProperty("selected_index", NullValueHandling = NullValueHandling.Ignore)] public List<string>? SelectedIndex { get; set; }
    [JsonProperty("index_tiers", NullValueHandling = NullValueHandling.Ignore)] public List<List<string>>? IndexTiers { get; set; }
    [JsonProperty("intent_enabled", NullValueHandling = NullValueHandling.Ignore)] public bool? IntentEnabled { get; set; }
    [JsonProperty("intent_tiers", NullValueHandling = NullValueHandling.Ignore)] public List<bool>? IntentTiers { get; set; }
    [JsonProperty("single_prompt", NullValueHandling = NullValueHandling.Ignore)] public string? SinglePrompt { get; set; }
    [JsonProperty("dsl", NullValueHandling = NullValueHandling.Ignore)] public string? Dsl { get; set; }
    [JsonProperty("document_ids", NullValueHandling = NullValueHandling.Ignore)] public List<string>? DocumentIds { get; set; }
    [JsonProperty("intent_context", NullValueHandling = NullValueHandling.Ignore)] public string? IntentContext { get; set; }
}

/// <summary>chat_room 串流塊的 data、兩支聊天室清單端點的每一列。這裡只列常用欄位；實際還有整份凍結設定。</summary>
public class ChatRoom
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("title")] public string? Title { get; set; }
    [JsonProperty("description")] public string? Description { get; set; }
    [JsonProperty("role")] public string? Role { get; set; }
    [JsonProperty("status")] public string? Status { get; set; }
    [JsonProperty("model_name")] public string? ModelName { get; set; }
    [JsonProperty("reasoning_effort")] public string? ReasoningEffort { get; set; }
    [JsonProperty("search_mode")] public string? SearchMode { get; set; }
    [JsonProperty("user_id")] public string? UserId { get; set; }
    [JsonProperty("tenant_id")] public string? TenantId { get; set; }
    [JsonProperty("tag")] public string? Tag { get; set; }
    [JsonProperty("config_name")] public string? ConfigName { get; set; }
    [JsonProperty("config_version")] public int? ConfigVersion { get; set; }
    [JsonProperty("selected_index")] public List<string>? SelectedIndex { get; set; }
    [JsonProperty("dsl")] public string? Dsl { get; set; }
    [JsonProperty("document_ids")] public List<string>? DocumentIds { get; set; }
    [JsonProperty("search_selected_number")] public int SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number")] public int SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio")] public float DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode")] public string? UseKnowledgeMode { get; set; }
    [JsonProperty("enable_rerank")] public bool EnableRerank { get; set; }
    [JsonProperty("memory_count")] public int MemoryCount { get; set; }
    [JsonProperty("response_format")] public string? ResponseFormat { get; set; }
    [JsonProperty("enable_suggest_questions")] public bool EnableSuggestQuestions { get; set; }
    [JsonProperty("temperature")] public float Temperature { get; set; }
    [JsonProperty("active_chain_end_id")] public int? ActiveChainEndId { get; set; }
    [JsonProperty("chat_logs_count")] public int? ChatLogsCount { get; set; }     // 查不動時是 null，不是 0
    [JsonProperty("created_at")] public DateTime CreatedAt { get; set; }
    [JsonProperty("updated_at")] public DateTime UpdatedAt { get; set; }

    // 以下只在 chat_room 串流塊出現
    [JsonProperty("latest_chat_log_id")] public int? LatestChatLogId { get; set; }
    [JsonProperty("suggest_questions")] public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")] public List<DocumentResult>? SearchResults { get; set; }
    [JsonProperty("grounding_no_answer")] public bool GroundingNoAnswer { get; set; }
    [JsonProperty("intent_index")] public string? IntentIndex { get; set; }
    [JsonProperty("usage")] public JObject? Usage { get; set; }                   // 鍵集開放，供應商未回報時 null
    [JsonProperty("latency_ms")] public int? LatencyMs { get; set; }
    [JsonProperty("ttft_ms")] public int? TtftMs { get; set; }
    [JsonProperty("answer_source")] public string? AnswerSource { get; set; }     // 只有 QA 直答那一輪有，值為 "qa_direct"
}

public class UserChatRoomsPage
{
    [JsonProperty("chat_rooms")] public List<ChatRoom> ChatRooms { get; set; } = new();
    [JsonProperty("total_count")] public int TotalCount { get; set; }
    [JsonProperty("returned_count")] public int ReturnedCount { get; set; }
    [JsonProperty("user_id")] public string? UserId { get; set; }
}

/// <summary>來源卡。last_modified 與 source_no 都可能是 null。</summary>
public class DocumentResult
{
    [JsonProperty("doc_name")] public string? DocName { get; set; }
    [JsonProperty("document_id")] public string? DocumentId { get; set; }
    [JsonProperty("chunk_index")] public int ChunkIndex { get; set; }
    [JsonProperty("data_source")] public string? DataSource { get; set; }
    [JsonProperty("index")] public string? Index { get; set; }
    [JsonProperty("search_mode")] public string? SearchMode { get; set; }         // weaviate_only / gufonet_only / hybrid
    [JsonProperty("last_modified")] public DateTime? LastModified { get; set; }   // 上游沒給就是 null，不會被填成現在
    [JsonProperty("score")] public float Score { get; set; }
    [JsonProperty("source_no")] public int? SourceNo { get; set; }               // 答案裡 [[N]] 指的卡；null＝模型沒看過
    [JsonProperty("document")] public Dictionary<string, object>? Document { get; set; }  // 鍵集隨索引而異
}

/// <summary>三支紀錄端點共用的每一則。這裡列常用欄位，完整欄位見 README。</summary>
public class ChatLog
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("chat_room_id")] public int ChatRoomId { get; set; }
    [JsonProperty("previous_chat_log_id")] public int? PreviousChatLogId { get; set; }
    [JsonProperty("human_content")] public string HumanContent { get; set; } = "";
    [JsonProperty("ai_content")] public string? AiContent { get; set; }
    [JsonProperty("thinking_content")] public string? ThinkingContent { get; set; }
    [JsonProperty("human_time")] public DateTime HumanTime { get; set; }
    [JsonProperty("ai_time")] public DateTime? AiTime { get; set; }
    [JsonProperty("suggest_questions")] public List<string>? SuggestQuestions { get; set; }
    [JsonProperty("search_results")] public List<DocumentResult>? SearchResults { get; set; }
    [JsonProperty("language")] public string? Language { get; set; }             // null＝本輪未做語言偵測
    [JsonProperty("query_start_time")] public DateTime? QueryStartTime { get; set; }
    [JsonProperty("query_end_time")] public DateTime? QueryEndTime { get; set; }
    [JsonProperty("keywords")] public List<string>? Keywords { get; set; }
    [JsonProperty("question")] public string? Question { get; set; }
    [JsonProperty("tag")] public string? Tag { get; set; }
    [JsonProperty("prompt_version")] public int? PromptVersion { get; set; }
    [JsonProperty("single_prompt")] public string? SinglePrompt { get; set; }
    [JsonProperty("rating_type")] public string? RatingType { get; set; }
    [JsonProperty("rating_feedback")] public string? RatingFeedback { get; set; }
    [JsonProperty("rating_time")] public DateTime? RatingTime { get; set; }
    [JsonProperty("input_tokens")] public int? InputTokens { get; set; }
    [JsonProperty("output_tokens")] public int? OutputTokens { get; set; }
    [JsonProperty("total_tokens")] public int? TotalTokens { get; set; }
    [JsonProperty("ttft_ms")] public int? TtftMs { get; set; }
    [JsonProperty("blocked_by")] public string? BlockedBy { get; set; }           // input_policy / output_policy / null
    [JsonProperty("answer_source")] public string? AnswerSource { get; set; }     // qa_direct / null
    [JsonProperty("search_mode")] public string? SearchMode { get; set; }         // traditional / agent
    [JsonProperty("step_events")] public JArray? StepEvents { get; set; }
    [JsonProperty("tool_activity")] public JArray? ToolActivity { get; set; }
    [JsonProperty("thinking_by_node")] public JObject? ThinkingByNode { get; set; }
}

public class ChatLogsPage
{
    [JsonProperty("chat_logs")] public List<ChatLog> ChatLogs { get; set; } = new();
    [JsonProperty("total_count")] public int TotalCount { get; set; }
    [JsonProperty("returned_count")] public int ReturnedCount { get; set; }
    [JsonProperty("limit")] public int Limit { get; set; }
    [JsonProperty("offset")] public int Offset { get; set; }
    [JsonProperty("filters_applied")] public JObject? FiltersApplied { get; set; }
}

public class RatingRequest
{
    [JsonProperty("rating_type")] public string RatingType { get; set; } = "positive";   // positive / negative
    [JsonProperty("feedback", NullValueHandling = NullValueHandling.Ignore)] public string? Feedback { get; set; }  // 上限 2000 字
}

public class RatingResponse
{
    [JsonProperty("chat_log_id")] public int ChatLogId { get; set; }
    [JsonProperty("tag")] public string? Tag { get; set; }
    [JsonProperty("prompt_version")] public int? PromptVersion { get; set; }
    [JsonProperty("single_prompt")] public string? SinglePrompt { get; set; }
    [JsonProperty("rating_type")] public string? RatingType { get; set; }
    [JsonProperty("rating_feedback")] public string? RatingFeedback { get; set; }
    [JsonProperty("rating_time")] public DateTime? RatingTime { get; set; }
}

public class UsageStatus
{
    [JsonProperty("current_usage")] public int CurrentUsage { get; set; }
    [JsonProperty("max_usage")] public int MaxUsage { get; set; }        // 0＝無限制
    [JsonProperty("remaining_usage")] public int RemainingUsage { get; set; }  // -1＝無限制
    [JsonProperty("is_allowed")] public bool IsAllowed { get; set; }
    [JsonProperty("is_unlimited")] public bool IsUnlimited { get; set; }
}

/// <summary>建立／更新設定時送的欄位（局部更新，只送要改的）。完整欄位以 GET /api/config/ 為準。</summary>
public class ConfigRequest
{
    [JsonProperty("product_system_prompt", NullValueHandling = NullValueHandling.Ignore)] public string? ProductSystemPrompt { get; set; }
    [JsonProperty("role", NullValueHandling = NullValueHandling.Ignore)] public string? Role { get; set; }
    [JsonProperty("model_name", NullValueHandling = NullValueHandling.Ignore)] public string? ModelName { get; set; }
    [JsonProperty("reasoning_effort", NullValueHandling = NullValueHandling.Ignore)] public string? ReasoningEffort { get; set; }
    [JsonProperty("search_selected_number", NullValueHandling = NullValueHandling.Ignore)] public int? SearchSelectedNumber { get; set; }
    [JsonProperty("search_total_number", NullValueHandling = NullValueHandling.Ignore)] public int? SearchTotalNumber { get; set; }
    [JsonProperty("data_source_ratio", NullValueHandling = NullValueHandling.Ignore)] public float? DataSourceRatio { get; set; }
    [JsonProperty("use_knowledge_mode", NullValueHandling = NullValueHandling.Ignore)] public string? UseKnowledgeMode { get; set; }
    [JsonProperty("enable_rerank", NullValueHandling = NullValueHandling.Ignore)] public bool? EnableRerank { get; set; }
    [JsonProperty("reranker_name", NullValueHandling = NullValueHandling.Ignore)] public string? RerankerName { get; set; }
    [JsonProperty("memory_count", NullValueHandling = NullValueHandling.Ignore)] public int? MemoryCount { get; set; }
    [JsonProperty("enable_suggest_questions", NullValueHandling = NullValueHandling.Ignore)] public bool? EnableSuggestQuestions { get; set; }
    [JsonProperty("response_format", NullValueHandling = NullValueHandling.Ignore)] public string? ResponseFormat { get; set; }
    [JsonProperty("temperature", NullValueHandling = NullValueHandling.Ignore)] public float? Temperature { get; set; }
    [JsonProperty("timezone", NullValueHandling = NullValueHandling.Ignore)] public string? Timezone { get; set; }
    [JsonProperty("document_field_mapping", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string>? DocumentFieldMapping { get; set; }
    [JsonProperty("selected_index", NullValueHandling = NullValueHandling.Ignore)] public List<string>? SelectedIndex { get; set; }
    [JsonProperty("search_mode", NullValueHandling = NullValueHandling.Ignore)] public string? SearchMode { get; set; }
    [JsonProperty("agent_max_iterations", NullValueHandling = NullValueHandling.Ignore)] public int? AgentMaxIterations { get; set; }
    [JsonProperty("enable_citation", NullValueHandling = NullValueHandling.Ignore)] public bool? EnableCitation { get; set; }
}

public class ConfigVersions
{
    [JsonProperty("versions")] public List<ConfigVersion> Versions { get; set; } = new();
}

public class ConfigVersion
{
    [JsonProperty("version_no")] public int VersionNo { get; set; }
    [JsonProperty("source")] public string? Source { get; set; }
    [JsonProperty("changed_fields")] public List<string> ChangedFields { get; set; } = new();
    [JsonProperty("created_at")] public DateTime CreatedAt { get; set; }
    [JsonProperty("is_current")] public bool IsCurrent { get; set; }
}

public class ModelCatalog
{
    [JsonProperty("models")] public List<ModelCatalogEntry> Models { get; set; } = new();
}

public class ModelCatalogEntry
{
    [JsonProperty("value")] public string Value { get; set; } = "";
    [JsonProperty("label")] public string Label { get; set; } = "";
    [JsonProperty("provider")] public string Provider { get; set; } = "";
    [JsonProperty("reasoning_options")] public List<string> ReasoningOptions { get; set; } = new();
    [JsonProperty("reasoning_default")] public string ReasoningDefault { get; set; } = "";
    [JsonProperty("omit_temperature")] public bool OmitTemperature { get; set; }
    [JsonProperty("adaptive_thinking")] public bool AdaptiveThinking { get; set; }
    [JsonProperty("max_output_tokens")] public int MaxOutputTokens { get; set; }
}
