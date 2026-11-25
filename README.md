# خادم ArcGIS Pro MCP

<p align="center">
  <a href="https://github.com/belalmahmoud2018/ArcGISPro-MCPServer/releases/latest">
    <img src="https://img.shields.io/badge/Download-ArcGISProMCP--Setup.exe-0079c1?style=for-the-badge&logo=windows&logoColor=white" alt="Download">
  </a>
  <br>
  <a href="https://github.com/belalmahmoud2018/ArcGISPro-MCPServer/releases/latest">
    <img src="https://img.shields.io/github/v/release/belalmahmoud2018/ArcGISPro-MCPServer?label=Latest%20release" alt="Latest release">
  </a>
  <img src="https://img.shields.io/badge/ArcGIS%20Pro-3.0%20%E2%80%93%203.7-2c7ac9" alt="ArcGIS Pro 3.0 - 3.7">
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078d6" alt="Windows 10 | 11">
</p>

> **⬇️ [اضغط هنا لتحميل `ArcGISProMCP-Setup.exe`](https://github.com/belalmahmoud2018/ArcGISPro-MCPServer/releases/latest)** ومعاه دليل المستخدم بالعربي PDF.

خادم **MCP** (Model Context Protocol) يخلّي أي وكيل ذكاء اصطناعي يتحكم في **ArcGIS Pro من 3.0 لحد 3.7**:
Claude Desktop و Claude Code و ChatGPT و Codex و Gemini CLI و Antigravity و Cursor و Windsurf و VS Code Copilot و Cline و Roo و Kiro و Qwen Code و LM Studio (موديلات DeepSeek وغيرها) و opencode و Amazon Q، وأي عميل MCP تاني.

المستخدم بيحتاج **ملف واحد بس: `ArcGISProMCP-Setup.exe`**. من غير Python ومن غير .NET ومن غير أي مكتبات ومن غير صلاحيات Administrator.

---

### التثبيت (للمستخدم النهائي)

1. اقفل ArcGIS Pro.
2. شغّل `ArcGISProMCP-Setup.exe` بدبل كليك.
3. هتظهر لك قائمة بوكلاء الذكاء الاصطناعي اللي اتلقوا على جهازك وعليهم علامة صح. اختار اللي انت عاوزه واضغط **Install / Update تثبيت**.
4. افتح ArcGIS Pro، هتلاقي تاب جديد اسمه **AI / MCP** وفيه زرار **MCP Status**.
5. اقفل برنامج الذكاء الاصطناعي وافتحه تاني (Claude Desktop أو Cursor مثلاً)، وبعدها اطلب منه أي حاجة. مثلاً:
   > «افتح ArcGIS Pro، ضيف طبقة المحافظات، لوّنها حسب عدد السكان، وطلّعلي Layout فيه عنوان ومفتاح خريطة وصدّره PDF».

> لو Pro مش بيحمّل الإضافة: من **Project ← Add-In Manager ← Options** اختار *Load all Add-Ins without restrictions*.

### ملاحظات لكل وكيل

| الوكيل | الطريقة |
|---|---|
| Claude Desktop و Claude Code و Codex و Gemini CLI و Antigravity و Cursor و Windsurf و VS Code و Cline و Roo و Kiro و Qwen و LM Studio و opencode و Amazon Q | إعداد تلقائي من المثبّت (stdio) |
| **ChatGPT** (Developer mode ← Connectors) | من نافذة البرنامج اضغط **Start HTTP server**، وبعدين اعمل نفق HTTPS زي `cloudflared tunnel --url http://localhost:3939` أو `ngrok http 3939`، وحط الرابط `https://<النفق>/mcp/<التوكن>` في ChatGPT واختار Authentication: None |
| DeepSeek وأي وكيل تاني (Cherry Studio و Chatbox و Continue و Zed و Goose وغيرهم) | زرار **Copy JSON config** وبعدين الصق الإعداد في الوكيل |

الإعداد العام لأي عميل:
```json
{ "mcpServers": { "arcgis-pro": { "command": "C:\\Users\\<user>\\AppData\\Local\\Programs\\ArcGISProMCP\\ArcGISProMCP.exe", "args": ["--stdio"] } } }
```

---

## إزاي بيشتغل

```
وكيل الذكاء الاصطناعي ──MCP (stdio / HTTP)──► ArcGISProMCP.exe ──Named Pipe محلي──► إضافة ArcGIS Pro (جوه Pro)
                                                                                  ├─ Pro SDK (خرائط، طبقات، Layouts، تعديل)
                                                                                  ├─ Geoprocessing (+1500 أداة)
                                                                                  ├─ ArcPy جوه Pro (aprx = "CURRENT")
                                                                                  └─ أي زرار في الـ Ribbon بالـ DAML ID
```

* الإضافة متبنية على **ArcGIS Pro SDK 3.0** (.NET 6)، وده معناه إنها شغالة على كل إصدارات 3.x (من 3.0 لـ 3.7)، ومنهم 3.5 وما بعدها اللي شغالين على .NET 8. اتجربت فعلاً على Pro 3.6.
* الاتصال محلي بس (Named Pipe خاص بالمستخدم). وضع HTTP بيشتغل على `127.0.0.1` بشكل افتراضي، ولو فتحته للشبكة لازم يكون معاه Token.

## «تحكم في كل حاجة» بالمعنى الحرفي

فيه 50 أداة. أهم أربع أدوات بيخلّوا الوكيل يقدر يعمل أي حاجة في البرنامج:

| الأداة | بتعمل إيه |
|---|---|
| `run_geoprocessing` | بتشغّل أي أداة Geoprocessing (Buffer و Clip و Spatial Analyst و Network ...) |
| `run_python` | بتشغّل كود ArcPy جوه Pro نفسه، والمتغيرات بتفضل محفوظة بين الأوامر |
| `search_commands` + `execute_command` | بيدوّروا على أي زرار في الـ Ribbon ويدوسوا عليه (كل الأزرار اللي في نسختك) |
| `get_layer_cim` / `set_layer_cim` | تحكم كامل في الرموز والتسميات والـ Popups |

وكمان فيه أدوات جاهزة: المشاريع، والخرائط، والتنقل، والـ Bookmarks، والطبقات وخصائصها، والاستعلام، والإحصاءات، والتحديد بالخصائص وبالموقع، وإنشاء وتعديل وحذف المعالم، والرموز (Unique و Graduated و Heatmap)، والتسميات، والـ Layouts والتصدير، والـ Undo والـ Redo، و**`capture_map_view`** اللي بترجّع صورة الخريطة للوكيل علشان «يشوف» النتيجة.

## أوامر سطر الأوامر

```
ArcGISProMCP.exe                 نافذة التثبيت والإدارة
ArcGISProMCP.exe --stdio         خادم MCP (العملاء بيستخدموه)
ArcGISProMCP.exe --http --port 3939 [--host 0.0.0.0 --token XYZ]
ArcGISProMCP.exe --install --clients detected|all|none|claude-desktop,cursor
ArcGISProMCP.exe --uninstall [--quiet]
ArcGISProMCP.exe --status | --list-tools | --print-config
```

## البناء (للمطوّر)

محتاج .NET 8 SDK بس (مش محتاج ArcGIS Pro علشان تبني):
```
powershell -ExecutionPolicy Bypass -File build.ps1
```
الناتج هيكون في `dist\ArcGISProMCP-Setup.exe` (حوالي 74 ميجا، ومعاه كل حاجة جواه).

* `src\ArcGISProMcp.AddIn` إضافة Pro (الجسر وأدوات التنفيذ).
* `src\ArcGISProMcp.Server` خادم MCP والمثبّت وإعداد الوكلاء.

السجلات موجودة في `%LOCALAPPDATA%\ArcGISProMCP\logs`.

