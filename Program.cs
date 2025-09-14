using System;
using System.Linq;
using System.Net;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DiscordRPC;
using Newtonsoft.Json.Linq;

namespace DiscordRPC_TrayApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayForm());
        }
    }

    public class RpcButtonData
    {
        public string label { get;set;}
        public string url { get;set;}
    }

    public class TrayForm : Form
    {
        private DiscordRpcClient rpcClient;
        private HttpListener httpListener;
        private Thread listenerThread;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private LogForm logForm;

        private const string urlPrefix = "http://127.0.0.1:8000/";
        private const string clientId = "1416838465768652961";

        public TrayForm()
        {
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Visible = false;

            InitializeTray();
            InitializeDiscordRPC();
            StartHttpServer();
        }

        private void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show Log", null, OnShowLog);
            trayMenu.Items.Add("Exit", null, OnExit);

            trayIcon = new NotifyIcon
            {
                Icon = new System.Drawing.Icon(GetType().Assembly.GetManifestResourceStream("DiscordRPC_TrayApp.ShadowRPC.ico")),
                ContextMenuStrip = trayMenu,
                Visible = true,
                Text = "Shadow RPC Server"
            };
        }

        private void OnShowLog(object sender, EventArgs e)
        {
            if (logForm == null || logForm.IsDisposed)
            {
                logForm = new LogForm();
                logForm.Show();
            }
            else
            {
                logForm.BringToFront();
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            httpListener.Stop();
            rpcClient.Dispose();
            trayIcon.Visible = false;
            Application.Exit();
        }

        private void InitializeDiscordRPC()
        {
            rpcClient = new DiscordRpcClient(clientId);
            rpcClient.Initialize();
            Log("Shadow RPC connected.");
        }

        private void StartHttpServer()
        {
            httpListener = new HttpListener();
            httpListener.Prefixes.Add(urlPrefix);

            listenerThread = new Thread(() =>
            {
                try
                {
                    httpListener.Start();
                    Log("HTTP server started at " + urlPrefix);

                    while (httpListener.IsListening)
                    {
                        try
                        {
                            var context = httpListener.GetContext();
                            HandleRequest(context);
                        }
                        catch (Exception ex)
                        {
                            Log("HTTP listener error: " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Failed to start HTTP listener: " + ex.Message);
                }
            });

            listenerThread.IsBackground = true;
            listenerThread.Start();
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            if (request.HttpMethod != "POST" || request.Url.AbsolutePath != "/update_rpc")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            string body;
            using (var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            try
            {
                JObject json = JObject.Parse(body);

                string state = json["activity_state"]?.ToString()
                               ?? json["activity_name"]?.ToString()
                               ?? "Playing Roblox";

                string details = json["activity_details"]?.ToString() ?? "";

                DiscordRPC.Button[] discordButtons = null;
                var buttonToken = json["buttons"];
                if (buttonToken != null && buttonToken.Type == JTokenType.Array)
                {
                    var buttons = buttonToken
                        .Where(t => t.Type == JTokenType.Object)
                        .Select(t => new RpcButtonData
                        {
                            label = t["label"]?.ToString() ?? "",
                            url = t["url"]?.ToString() ?? ""
                        })
                        .Where(b => !string.IsNullOrEmpty(b.label) && !string.IsNullOrEmpty(b.url))
                        .Take(2) // max 2 buttons
                        .Select(b => new DiscordRPC.Button { Label = b.label, Url = b.url })
                        .ToArray();

                    if (buttons.Length > 0)
                        discordButtons = buttons;
                }

                Timestamps timestamps = null;
                if (json["start_timestamp"] != null)
                {
                    long startSec = json["start_timestamp"].Value<long>();
                    timestamps = new Timestamps(DateTimeOffset.FromUnixTimeSeconds(startSec).UtcDateTime);
                }

                if (json["end_timestamp"] != null)
                {
                    long endSec = json["end_timestamp"].Value<long>();
                    if (timestamps == null)
                        timestamps = new Timestamps(DateTime.UtcNow);
                    timestamps.End = DateTimeOffset.FromUnixTimeSeconds(endSec).UtcDateTime;
                }

                rpcClient.SetPresence(new RichPresence
                {
                    State = state,
                    Details = details,
                    Timestamps = timestamps,
                    Buttons = discordButtons,
                });

                Log($"Update: State='{state}', Details='{details}', Buttons='{discordButtons}, ButtonCount='{discordButtons.Length}'");

                var buffer = Encoding.UTF8.GetBytes("{\"message\":\"RPC updated successfully\"}");
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.StatusCode = 200;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                Log("Error handling request: " + ex.Message);
                response.StatusCode = 500;
                response.Close();
            }
        }

        private void Log(string message)
        {
            Console.WriteLine(message);
            if (logForm != null && !logForm.IsDisposed)
            {
                logForm.AppendLog(message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            httpListener.Stop();
            rpcClient.Dispose();
            trayIcon.Visible = false;
            base.OnFormClosing(e);
        }
    }

    public class LogForm : Form
    {
        private TextBox logBox;

        public LogForm()
        {
            this.Text = "Shadow RPC | Log";
            this.Size = new System.Drawing.Size(600, 400);
            this.Icon = new System.Drawing.Icon(GetType().Assembly.GetManifestResourceStream("DiscordRPC_TrayApp.ShadowRPC.ico"));

            logBox = new TextBox
            {
                Multiline = true,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical
            };

            this.Controls.Add(logBox);
        }

        public void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action<string>(AppendLog), message);
                return;
            }

            logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
    }
}
