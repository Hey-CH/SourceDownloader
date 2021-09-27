using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Xml.Serialization;

namespace SourceDownloader {
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window {
        private ViewModel vm;
        string _DownloadDir;
        bool navigateOnly = false;
        bool executeJs = false;//javascript実行モード
        string exeJavaScript = "";//実行するjavascript
        int patrolBefore = -1;//getHrefSrcListが帰ってきたときに確認するPos
        bool survive = true;
        private string DownloadDir {
            get {
                if(string.IsNullOrEmpty(_DownloadDir))
                    _DownloadDir = System.IO.Path.Combine(vm != null ? vm.OutDir : AppDomain.CurrentDomain.BaseDirectory, "download");
                return _DownloadDir;
            }
        }
        private string SettingPath {
            get {
                return System.IO.Path.Combine(vm != null ? vm.OutDir : AppDomain.CurrentDomain.BaseDirectory, "setting.xml"); ;
            }
        }
        private string LogPath {
            get {
                return System.IO.Path.Combine(vm != null ? vm.OutDir : AppDomain.CurrentDomain.BaseDirectory, "failed.log"); ;
            }
        }

        public MainWindow() {
            InitializeComponent();

            InitWebView2();

            vm = new ViewModel();
            vm.PropertyChanged += (s, e) => {
                if (e.PropertyName == nameof(vm.OutDir) && File.Exists(SettingPath)) {
                    if (MessageBox.Show("Do you want to resume previous download?", "Resume confirm", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) {
                        var xs = new XmlSerializer(typeof(ViewModel));
                        using (var fs = new FileStream(SettingPath, FileMode.Open, FileAccess.Read)) {
                            var tmp = (ViewModel)xs.Deserialize(fs);
                            var r = vm.Ready;
                            tmp.Ready = r;//デフォがfalseなので手動でやってあげないといけない
                            vm = tmp;
                            this.DataContext = vm;
                        }
                    }
                }
            };
            if (File.Exists(SettingPath)) {
                if (MessageBox.Show("Do you want to resume previous download?", "Resume confirm", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) {
                    var xs = new XmlSerializer(typeof(ViewModel));
                    using (var fs = new FileStream(SettingPath, FileMode.Open, FileAccess.Read)) {
                        vm = (ViewModel)xs.Deserialize(fs);
                    }
                }
            }

            this.DataContext = vm;

            //巡回スレッド
            Task.Run(() => {
                while (survive) {
                    if (vm.PatrolPos < vm.PatrolURLList.Count && vm.Ready && !string.IsNullOrEmpty(vm.PatrolURLList[vm.PatrolPos])) {
                        try {
                            if (patrolBefore == vm.PatrolPos) continue;
                            patrolBefore = vm.PatrolPos;
                            errCount = 0;
                            var url = vm.PatrolURLList[vm.PatrolPos];
                            Debug.Print(url);
                            if (url.StartsWith(javascript)) {
                                exeJavaScript = GetJavaScriptFormMsg(url);
                                if (IsIgnoreJavaScript(exeJavaScript)) {
                                    vm.PatrolPos++;
                                    continue;
                                }
                                executeJs = true;
                                url = url.Substring(0, url.IndexOf('|')).Replace(javascript, "");
                            } else executeJs = false;
                            //js実行時、表示中のURLとurlが同じならNavigateしない
                            Dispatcher.Invoke(() => {
                                if (executeJs && GetWebView2URL().ToLower() == url.ToLower()) {
                                    vm.Ready = false;//NavigateしなくてもURLが変わる可能性があるため設定
                                    CoreWebView2_NavigationCompleted(webView2, null);
                                    executeJs = false;
                                } else
                                    Navigate(url);
                            });
                            System.Threading.Thread.Sleep(100);
                        } catch { }
                    }
                }
            });

            //ダウンロードスレッド
            Task.Run(() => {
                while (survive) {
                    if (vm.DownloadPos <= vm.DownloadList.Count - 1) {
                        try {
                            Download(vm.DownloadList[vm.DownloadPos]);
                            vm.DownloadPos++;
                            System.Threading.Thread.Sleep(100);

                            //ダウンロード完了時メッセージボックス表示
                            if (vm.DownloadPos >= vm.DownloadList.Count && vm.PatrolPos >= vm.PatrolURLList.Count)
                                MessageBox.Show("Download completed.");
                        } catch { }
                    }
                }
            });

            //表示用スレッド
            Task.Run(() => {
                while (survive) {
                    Dispatcher.Invoke(() => {
                        vm.DownloadStatus = vm.DownloadPos + "/" + vm.DownloadList.Count;
                        vm.PatrolStatus = vm.PatrolPos + "/" + vm.PatrolURLList.Count;
                    });
                    System.Threading.Thread.Sleep(100);
                }
            });
        }

        private async void InitWebView2() {
            await webView2.EnsureCoreWebView2Async(null);
            //coreWV2.DownloadStarting += CoreWV2_DownloadStarting;//これは右クリックして保存したときのやつみたい
            await webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(OnLoadEvent);//ロード完了時にメッセージ送信//何故か複数回呼ばれるのでNavigationCompletedに変更
            await webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetHrefSrcListFunction);//a要素のhref属性値をメッセージ送信
            await webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetHTML);//HTMLを取得
            webView2.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView2.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            vm.Ready = true;
        }

        string lastExecJs = "";
        private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e) {
            if (executeJs && !Regex.IsMatch(exeJavaScript,"alert\\s*\\(",RegexOptions.IgnoreCase)) {
                var js = "var u1=document.location.href;"
                        + "try{"+(exeJavaScript.EndsWith(';') ? exeJavaScript : exeJavaScript + ";")+"}catch{}"
                        + "var u2=document.location.href;"
                        + "if(u2!=u1)window.chrome.webview.postMessage('href changed.');"
                        + "else window.chrome.webview.postMessage('href not changed.');";
                if (lastExecJs != js) {
                    lastExecJs = js;
                    await webView2.CoreWebView2.ExecuteScriptAsync(js);
                }else
                    await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");

            } else
                vm.Ready = true;
        }

        int waitCount = 0;
        private delegate Task Method();
        private Method execMsg;
        string lastMsg = "";
        int errCount = 0;
        private async void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
            if (navigateOnly) return;
            try {
                var msg = e.TryGetWebMessageAsString();
                if (msg.StartsWith(url)) {
                    lastMsg = msg;
                    //Messageを受信したらメッセージ実行タスクを起動
                    if (execMsg == null) {
                        execMsg = ExecMsgAfterWait;
                        execMsg();
                    } else {
                        waitCount = 0;
                    }
                } else {
                    if(msg == "href not changed.") await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");
                    else MessageBox.Show(msg);
                }
            } catch(ArgumentException aex) {
                //メッセージが取得できない場合、仕方ないのでもう1度取得
                errCount++;
                if (errCount < 10) {
                    await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");
                } else vm.PatrolPos++;//どうしても取得できない場合は無視するしかない。
            }
        }
        private async Task ExecMsgAfterWait() {
            await Task.Run(async () => {
                while (waitCount < 15) {
                    System.Threading.Thread.Sleep(100);
                    waitCount++;
                }
                waitCount = 0;
                await ExecLastMsg();
            });
        }
        private async Task ExecLastMsg() {
            execMsg = null;
            try {
                if (lastMsg.StartsWith(url)) {
                    //msgは<<<url>>>http://~~~\n<<<hreflist>>>\n...\n<<<srclist>>>\n...\nという感じになっている
                    var idx = lastMsg.IndexOf(hreflist);
                    var urlmsg = lastMsg.Substring(0, idx - 1).Replace(url, "");
                    vm.URL = urlmsg;
                    if (GetWebView2URL() != urlmsg) {
                        //getHrefSrcListで取得したURLとwebView2のURLが違っていたら再度取得
                        await Task.Delay(5000);
                        await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");
                        return;
                    }

                    lastMsg = lastMsg.Substring(idx);
                    idx = lastMsg.IndexOf(srclist);
                    var hrefsmsg = lastMsg.Substring(0, idx - 1);
                    var srcsmsg = lastMsg.Substring(0, lastMsg.Length - 1).Substring(idx);
                    var hrefs = hrefsmsg.Split('\n').Skip(1).ToList();
                    var srcs = srcsmsg.Split('\n').Skip(1).ToList();

                    //巡回リスト作成
                    CheckAndAddPatrolURLList(hrefs);
                    //MessageBox.Show(string.Join('\n', vm.PatrolURLList));

                    //ダウンロードリスト作成
                    AddDownloadList(srcs);
                } else {
                    //MessageBox.Show(msg);
                }
                //メッセージが返ってきたらPatrolPosを１つ進める
                if (!navigateOnly && patrolBefore <= vm.PatrolPos)
                    vm.PatrolPos = patrolBefore + 1;
            } catch { } finally { vm.Ready = true; }
        }

        private void Button_Click(object sender, RoutedEventArgs e) {
            navigateOnly = true;
            Navigate(vm.RealURL);
        }

        private async void playBtn_Click(object sender, RoutedEventArgs e) {
            vm.FirstPatrolURLs.Add(vm.RealURL);
            navigateOnly = false;
            //[2021/09/10]巡回開始する時、UI上のURLとWebView2のURIが同じ場合、そのまま巡回開始する
            //[2021/09/11]WebView2が何か表示している場合（Sourceに値が入っている場合）それを巡回する
            //※ブラウザに表示されているURLを入力しても同じ表示にならない場合があるため 
            if (!GetWebView2URL().StartsWith("about:")) {
                vm.URL = GetWebView2URL();
                await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");
            } else
                Navigate(vm.RealURL);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter && vm.Ready) {
                navigateOnly = true;
                Navigate(vm.RealURL);
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e) {
            survive = false;
            try {
                if (File.Exists(vm.DownloadingPath)) File.Delete(vm.DownloadingPath);//ダウンロード途中のファイルは消す
            } catch { }
            var xs = new XmlSerializer(typeof(ViewModel));
            using (var fs = new FileStream(SettingPath, FileMode.Create, FileAccess.Write)) {
                xs.Serialize(fs, vm);
            }
        }

        private bool Navigate(string url) {
            if (!vm.Ready) return false;
            Dispatcher.Invoke(() => {
                if (vm.URL != url) vm.URL = url;
                webView2.CoreWebView2.Navigate(vm.RealURL);
                vm.Ready = false;
            });
            return true;
        }

        //正の整数のみ入力OK
        private void TextBox_TextChanged(object sender, TextChangedEventArgs e) {
            var r = new Regex("[^0-9]+");
            var tb = sender as TextBox;
            if (tb == null) return;
            var val = tb.Text;
            val = r.Replace(tb.Text, "");
            var tmp = 0;
            if (int.TryParse(val, out tmp)) {
                if (tmp >= 0) tb.Text = tmp.ToString();
                else tb.Text = 0.ToString();
            } else
                tb.Text = "";
        }

        /// <summary>
        /// urlsが有効なURLである場合、PatrolURLListに追加します
        /// </summary>
        /// <param name="urls">巡回すべきかどうか不明のURLのリスト</param>
        public void CheckAndAddPatrolURLList(List<string> urls) {
            var hosts = vm.FirstPatrolURLs.Select(u => new Uri(u).Host.ToLower());//new Uri(vm.FirstPatrolURL).Host.ToLower();
            var baseUri = new Uri(vm.RealURL);
            var addURLs = new Collection<string>();//後でまとめてInsertするための入れ物
            foreach (var u in urls) {
                //[2021/09/15]javascriptのonclick対応
                if (u.StartsWith(javascript)) {
                    if (!vm.PatrolURLList.Contains(u)) addURLs.Add(u);
                    continue;
                }

                bool issrc = false;
                var url = u;
                if (url.StartsWith(src)) {
                    issrc = true;
                    url = url.Substring(src.Length);
                }

                Uri otherUri = null;
                try { otherUri = new Uri(baseUri, url); }
                catch (Exception ex) { Debug.Print(ex.GetType().ToString() + " " + ex.Message); continue; }

                //[2021/09/09]HrefDownloadCondition追加（hrefでもこの条件にマッチする場合Downloadする）
                //ダウンロードするものはホストが違ってもダウンロードするため先にチェック
                if (CheckConditions(otherUri.AbsoluteUri, vm.HrefDownloadConditions)) {
                    if (!vm.DownloadList.Contains(otherUri.AbsoluteUri)) vm.DownloadList.Add(otherUri.AbsoluteUri);
                    continue;
                }

                if (!hosts.Contains(otherUri.Host.ToLower())) continue;//ホストが違う場合巡回しない
                //otherUriがルートを示していれば巡回しない
                if (IsBaseRoot(baseUri, otherUri)) continue;

                //[2021/09/10]URLに#が入っていたらページ内リンクと思っていたが、そうでない場合もあることが判明（https://～～～/#/aiueo/abc.html みたいな感じのもある）
                //従って、URLの最後の「/」以降の文字列に#が入っていたらページ内リンクとする
                if (otherUri.AbsoluteUri.Contains('#')
                    && otherUri.AbsoluteUri.Substring(otherUri.AbsoluteUri.LastIndexOf('/')).Contains("#")) continue;//Page内リンクはNavigatedイベントが発生しないので無視（id記法の場合はそもそも取得しない）

                var check = true;
                if (vm.CheckAll || !issrc) {//a要素がsrcを持つ要素の先祖である場合基本的に追加する（SourceDownloaderなので。）
                    check = CheckConditions(otherUri.AbsoluteUri, vm.PatrolConditions);
                }
                //無視もせず、巡回したリストにも、巡回するリストにも無い場合追加
                if (check
                    && !vm.FirstPatrolURLs.Select(u=>u.ToLower()).Contains(otherUri.AbsoluteUri.ToLower())
                    && !vm.PatrolURLList.Contains(otherUri.AbsoluteUri)
                    && !addURLs.Contains(otherUri.AbsoluteUri)) {
                    //Appendすると、関係ないページの後にPatrolする事になるので、現在見ているページの直後に追加する
                    //vm.PatrolURLList.Add(otherUri.AbsoluteUri);
                    addURLs.Add(otherUri.AbsoluteUri);
                }
            }

            if (addURLs.Count > 0)
                vm.PatrolURLList.InsertRange(GetInsertPos(), addURLs);
        }

        public void AddDownloadList(List<string> urls) {
            var baseUri = new Uri(vm.RealURL);
            foreach (var url in urls) {
                Uri otherUri = null;
                try { otherUri = new Uri(baseUri, url); }
                catch (Exception ex) { Debug.Print(ex.GetType().ToString() + " " + ex.Message); continue; }
                if (CheckConditions(otherUri.AbsoluteUri, vm.DownloadConditions) && !vm.DownloadList.Contains(otherUri.AbsoluteUri))
                    vm.DownloadList.Add(otherUri.AbsoluteUri);
            }
        }
        private bool CheckConditions(string absoluteUri, string condition) {
            //[2021/09/27]単純に正規表現のCheckだけにしました。
            return Regex.IsMatch(absoluteUri, condition, RegexOptions.IgnoreCase);
        }
        private void Download(string src) {
            if (string.IsNullOrEmpty(src)) throw new ArgumentNullException("invalid source");//DownloadListの最後を参照している場合、稀にnullになる事がある
            if (src.StartsWith("data:")) {//data:image/png;base64,efajeofijawoeifhapouwhfopwheo…みたいなのがある
                try {
                    var m = Regex.Match(src, "data:(.+);(.+),(.+)");
                    var type = m.Groups[1].Value;
                    var code = m.Groups[2].Value;
                    var data = m.Groups[3].Value;
                    var path = GetSavePath("data." + type.Split('/')[1]);
                    if (File.Exists(path)) return;
                    using (var ms = new MemoryStream(Convert.FromBase64String(data))) {
                        var bmp = Bitmap.FromStream(ms);
                        bmp.Save(path);
                    }
                } finally {
                    vm.DownloadingPath = null;
                }
            } else {
                try {
                    Uri uri = new Uri(src);
                    var path = GetSavePath(uri.LocalPath);
                    if (File.Exists(path)) return;//同ファイルが有る場合何もしない
                    vm.DownloadingPath = path;
                    using (var wc = new WebClient()) {
                        wc.DownloadFile(src, path);
                    }
                } catch (Exception ex) {
                    //404 Not found の時無限ループしちゃうのでその対応
                    //と思ったけど、失敗したら全部無視で良いや
                    //if (!ex.Message.Contains(IgnoreMsgs)) throw;
                    File.AppendAllText(LogPath, $"[{DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")}]" + ex.GetType().ToString() + "\t" + ex.Message + "\t" + src + "\r\n");
                } finally {
                    vm.DownloadingPath = null;
                }
            }
        }
        private string GetSavePath(string name) {
            if (name.StartsWith("/")) name = name.Substring(1);//nameの先頭が/だとルート相対パスになるので削除
            if (name.Contains("/")) name = name.Replace("/", "\\");//やらなくても大丈夫だけど一応置換しとく
            var path = System.IO.Path.Combine(DownloadDir, name);
            if (File.Exists(path)) {
                var n = name;
                var e = "";
                try {
                    n = name.Substring(0, name.LastIndexOf('.'));
                    e = name.Substring(name.LastIndexOf('.') + 1);
                } catch { }
                var i = 0;
                while (File.Exists(path)) {
                    path = System.IO.Path.Combine(DownloadDir, n + (++i) + "." + e);
                }
            }
            var dir = new FileInfo(path).DirectoryName;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return path;
        }
        /// <summary>
        /// otherUriがbaseUriのルート（http://www.aaa.com）と同じならtrueを返す
        /// </summary>
        /// <param name="baseUri">元となるUri</param>
        /// <param name="otherUri">調べたいUri</param>
        /// <returns></returns>
        private bool IsBaseRoot(Uri baseUri, Uri otherUri) {
            var baseRoot = baseUri.GetLeftPart(UriPartial.Authority);
            var other = otherUri.AbsoluteUri;
            if (other.Contains("#")) {
                if (other.Substring(other.LastIndexOf('/')).Contains('#'))
                    other = other.Substring(0, other.LastIndexOf('#'));
            }
            if (other.EndsWith('/')) other = other.Substring(0, other.Length - 1);
            return baseRoot.ToLower() == other.ToLower();
        }
        /// <summary>
        /// vm.PatrolUrlListのどの位置に挿入するか取得する関数
        /// </summary>
        private int GetInsertPos() {
            var ret = vm.PatrolPos + 1;
            if (ret < vm.PatrolURLList.Count) {
                while (vm.PatrolURLList[ret].StartsWith(javascript))
                    ret++;
            }
            return ret;
        }
        private string GetWebView2URL() {
            var url = "";
            Dispatcher.Invoke(() => { url = webView2.CoreWebView2.Source; });
            return url;
        }
        private bool IsIgnoreJavaScript(string js) {
            var ijs = false;
            Dispatcher.Invoke(() => { ijs = vm.IgnoreJavaScript; });
            if(ijs) return ijs;

            //vm.JavaScriptConditionsが設定されている場合、マッチした物だけ実行する
            if (!string.IsNullOrEmpty(vm.JavaScriptConditions.Trim()) && Regex.IsMatch(js, vm.JavaScriptConditions))
                return false;
            return true;
        }
        private string GetJavaScriptFormMsg(string msg) {
            return Regex.Replace(msg.Substring(msg.IndexOf('|') + 1), "return\\s*[\\;]*;?", "", RegexOptions.IgnoreCase);
        }

        #region statics
        static string url = "<<<url>>>";
        static string onload = "<<<onload>>>";
        static string hreflist = "<<<hreflist>>>";
        static string srclist = "<<<srclist>>>";
        static string src = "<<<src>>>";//a要素の子孫にsrc属性を持つ要素が存在する場合に追加するやつ（ホストが同じなら巡回する）
        static string javascript = "<<<javascript>>>";//onclickでjavascriptを実行して遷移する場合、そのURLとメソッドを保持する必要がある
        //iframeのsrcは巡回する必要があるのでhrefとして追加し、ダウンロード用のsrcからは省く（何が入ってるか不明なのでsrcを含むa要素と同様に確実に巡回するようにする）
        static string GetHrefSrcListFunction {
            get {
                return "function getHrefSrcList(){"
                            + "var hrefs='"+url+"'+document.location.href+'\\n';"
                            + "hrefs+='" + hreflist + "\\n';"

                            + "hrefs+=getOnClickTargets(document.body);"

                            + "var as=document.getElementsByTagName('a');"
                            + "for(var i=0;i<as.length;i++){"
                                + "if(hasDescendantsSrcElement(as[i]))"
                                    + "hrefs+='" + src + "'+as[i].getAttribute('href')+'\\n';"
                                + "else "
                                    + "hrefs+=as[i].getAttribute('href')+'\\n';"
                            + "}"

                            + "var iframes=document.getElementsByTagName('iframe');"
                            + "for(var i=0;i<iframes.length;i++){"
                                + "if(iframes[i].hasAttribute('src')){"
                                     + "hrefs+='" + src + "'+iframes[i].getAttribute('src')+'\\n';"
                                + "}"
                            + "}"

                            + "var srcs='" + srclist + "\\n';"
                            + "srcs+=getDescendantsSrcValues(document.body);"
                            + "window.chrome.webview.postMessage(hrefs + srcs);"
                        + "}"
                        + "function hasDescendantsSrcElement(ele){"
                            + "if(!ele.hasChildNodes())return false;"
                            + "var cs=ele.childNodes;"
                            + "for(var i=0;i<cs.length;i++){"
                                + "if(cs[i].nodeType!=1) continue;"
                                + "if(cs[i].hasAttribute('src')) return true;"
                                + "if(hasDescendantsSrcElement(cs[i])) return true;"
                            + "}"
                        + "}"
                        + "function getOnClickTargets(ele){"
                            + "var ret='';"
                            + "if(ele.hasAttribute('onclick')){"
                                + "ret+='"+javascript+ "'+document.location.href+'|'+ele.getAttribute('onclick')+'\\n';"
                                + "return ret;"
                            + "}"
                            + "if(!ele.hasChildNodes())return ret;"
                            + "var cs=ele.childNodes;"
                            + "for(var i=0;i<cs.length;i++){"
                                + "if(cs[i].nodeType!=1) continue;"
                                + "ret+=getOnClickTargets(cs[i]);"
                            + "}"
                            + "return ret;"
                        + "}"
                        + "function getDescendantsSrcValues(ele){"
                            + "var ret='';"
                            + "if(!ele.hasChildNodes())return ret;"
                            + "var cs=ele.childNodes;"
                            + "for(var i=0;i<cs.length;i++){"
                                + "if(cs[i].nodeType!=1) continue;"
                                + "if(cs[i].hasAttribute('src') && cs[i].tagName.toLowerCase()!='iframe'){"
                                    + "ret+=cs[i].getAttribute('src')+'\\n';"
                                + "}"
                                + "ret+=getDescendantsSrcValues(cs[i]);"
                            + "}"
                            + "return ret;"
                        + "}";
            }
        }
        static string OnLoadEvent {
            get {
                return "document.addEventListener('DOMContentLoaded', function() {"
                            + "window.chrome.webview.postMessage(getHrefSrcList());"
                        + "}, { once: true });";
                //return "var h='';"
                //        + "window.addEventListener('load', function () {"
                //            + "if (document.readyState === 'complete' && h!=document.location.href) {"
                //                + "h=document.location.href;"
                //                + "window.chrome.webview.postMessage('<<<onload>>>');"
                //            + "}"
                //        + "});";
                //return "window.addEventListener('popstate', (event) => {"
                //        + "window.chrome.webview.postMessage('<<<onload>>>');"
                //        + "});"
                //        + "const pushUrl = (href) => {"
                //            + "history.pushState({}, '', href);"
                //            + "window.dispatchEvent(new Event('popstate'));"
                //        + "};";
            }
        }
        static string GetHTML {
            get {
                return "function getHtml(){"
                        + "window.chrome.webview.postMessage(document.getElementsByTagName('html')[0].outerHTML);"
                        + "}";
            }
        }
        //このメッセージがエラーメッセージに入っているとダウンロードを中止して次に進む
        static string[] IgnoreMsgs {
            get {
                return new string[]{
                                    "404",
                                    "An error occurred while sending the request.",
                                    "An exception occurred during a WebClient request."
                                    };
            }
        }
        #endregion
    }
    public class ViewModel : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        string _OutDir = Properties.Settings.Default.OutDir;
        [XmlIgnore]
        public string OutDir {
            get {
                if (string.IsNullOrEmpty(_OutDir))
                    return AppDomain.CurrentDomain.BaseDirectory;
                return _OutDir;
            }
            set {
                _OutDir = value;
                Properties.Settings.Default.OutDir = value;
                Properties.Settings.Default.Save();
                OnPropertyChanged(nameof(OutDir));
            }
        }

        bool _Ready = false;
        [XmlIgnore]
        public bool Ready {
            get { return _Ready; }
            set {
                _Ready = value;
                OnPropertyChanged(nameof(Ready));
            }
        }
        string _URL = "";
        public string URL {
            get { return Uri.UnescapeDataString(_URL); }
            set {
                _URL = value;
                OnPropertyChanged(nameof(URL));
            }
        }
        [XmlIgnore]
        public string RealURL {
            get {
                if (Regex.IsMatch(_URL, @"[^-\]_.~!*'();:@&=+$,/?%#[A-z0-9]"))
                    return Uri.EscapeDataString(_URL);
                else
                    return _URL;
            }
        }
        bool _CheckAll = false;//PatrolConditionをa要素の子孫にsrcを持つ要素がいたとしても適用するかどうかを指定
        public bool CheckAll {
            get { return _CheckAll; }
            set {
                _CheckAll = value;
                OnPropertyChanged(nameof(CheckAll));
            }
        }
        string _PatrolConditions = "";
        public string PatrolConditions {
            get {
                return _PatrolConditions;
            }
            set {
                _PatrolConditions = value;
            }
        }
        string _DownloadConditions = @"(?!\.js$)";
        public string DownloadConditions {
            get {
                return _DownloadConditions;
            }
            set {
                _DownloadConditions = value;
            }
        }
        string _HrefDownloadConditions = @"\.(jpg|jpeg|png|bmp|gif|tiff|svg|psd|pdf|webp|zip|mp4|mov)$";
        public string HrefDownloadConditions {
            get {
                return _HrefDownloadConditions;
            }
            set {
                _HrefDownloadConditions = value;
            }
        }
        bool _IgnoreJavaScript = false;
        public bool IgnoreJavaScript {
            get { return _IgnoreJavaScript; }
            set {
                _IgnoreJavaScript = value;
                OnPropertyChanged(nameof(IgnoreJavaScript));
            }
        }
        string _JavaScriptConditions = "";
        public string JavaScriptConditions {
            get { return _JavaScriptConditions; }
            set {
                _JavaScriptConditions = value;
                OnPropertyChanged(nameof(JavaScriptConditions));
            }
        }

        public string DownloadingPath = null;//ダウンロード中のファイルパス（終了時＆起動時これがnullでない場合削除する）

        public List<string> FirstPatrolURLs { get; set; } = new List<string>();//ユーザーが最初に再生ボタンを押してダウンロード開始したURLのリスト
        
        int _PatrolPos = 0;
        public int PatrolPos {//PatrolURLListの何番目を巡回中か
            get { return _PatrolPos; }
            set {
                _PatrolPos = value;
                OnPropertyChanged(nameof(PatrolPos));
            }
        }
        public ObservableCollection<string> PatrolURLList { get; set; } = new ObservableCollection<string>();//巡回するURLのリスト

        int _DownloadPos = 0;
        public int DownloadPos {//DownloadListの何番目をダウンロード中か
            get { return _DownloadPos; }
            set {
                _DownloadPos = value;
                OnPropertyChanged(nameof(DownloadPos));
            }
        }
        public ObservableCollection<string> DownloadList { get; set; } = new ObservableCollection<string>();//ダウンロードするファイルのリスト

        string _PatrolStatus = "";
        [XmlIgnore]
        public string PatrolStatus {
            get { return _PatrolStatus; }
            set {
                _PatrolStatus = value;
                OnPropertyChanged(nameof(PatrolStatus));
            }
        }
        string _DownloadStatus = "";
        [XmlIgnore]
        public string DownloadStatus {
            get { return _DownloadStatus; }
            set {
                _DownloadStatus = value;
                OnPropertyChanged(nameof(DownloadStatus));
            }
        }
    }
    public class ProgressMaximumConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            var cnt = (int)value;
            if (cnt == 0) return 1;
            else return cnt;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            return value;
        }
    }

    public static class Common {
        public static void AddRange<T>(this Collection<T> source, Collection<T> items) {
            foreach (var i in items) source.Add(i);
        }
        public static void InsertRange<T>(this Collection<T> source,int index, Collection<T> items) {
            if (index >= source.Count)
                source.AddRange(items);
            else
                foreach (var i in items) source.Insert(index++, i);
        }
        public static bool Contains(this string text, IEnumerable<string> list, bool ignoreCase = true) {
            var tmp1 = ignoreCase ? list.Select(t => t.ToLower()) : list;
            var tmp2 = ignoreCase ? text.ToLower() : text;
            return tmp1.Any(t => tmp2.Contains(t));
        }
    }

}
