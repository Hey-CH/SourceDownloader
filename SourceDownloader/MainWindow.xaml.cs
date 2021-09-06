using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
        private string dlDir {
            get {
                if(string.IsNullOrEmpty(_DownloadDir))
                    _DownloadDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "download");
                return _DownloadDir;
            }
        }
        private string SettingPath {
            get {
                return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setting.xml"); ;
            }
        }

        public MainWindow() {
            InitializeComponent();

            InitWebView2();

            vm = new ViewModel();
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
                while (true) {
                    if (vm.PatrolPos <= vm.PatrolURLList.Count - 1 && vm.Ready && !string.IsNullOrEmpty(vm.PatrolURLList[vm.PatrolPos])) {
                        try {
                            if (Navigate(vm.PatrolURLList[vm.PatrolPos])) {
                                vm.PatrolPos++;
                                System.Threading.Thread.Sleep(100);
                            }
                        } catch { }
                    }
                }
            });

            //ダウンロードスレッド
            Task.Run(() => {
                while (true) {
                    if (vm.DownloadPos <= vm.DownloadList.Count - 1) {
                        try {
                            Download(vm.DownloadList[vm.DownloadPos]);
                            vm.DownloadPos++;
                        } catch { }
                    }
                }
            });
        }

        private async void InitWebView2() {
            await webView2.EnsureCoreWebView2Async(null);
            //coreWV2.DownloadStarting += CoreWV2_DownloadStarting;//これは右クリックして保存したときのやつみたい
            //await webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(OnLoadEvent);//ロード完了時にメッセージ送信//何故か複数回呼ばれるのでNavigationCompletedに変更
            await webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetHrefSrcListFunction);//a要素のhref属性値をメッセージ送信
            await webView2.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(GetHTML);//HTMLを取得
            webView2.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView2.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            vm.Ready = true;
        }

        private async void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e) {
            await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e) {
            string msg = "";
            try {
                msg = e.TryGetWebMessageAsString();
                if (msg.StartsWith(onload)) {//とりあえず廃止
                    //await webView2.CoreWebView2.ExecuteScriptAsync("getHrefSrcList();");
                    //await webView2.CoreWebView2.ExecuteScriptAsync("getHtml();");//2連続で実行すると実行されるようなされないようなダメダメになる
                } else if (msg.StartsWith(hreflist)) {
                    //msgは<<<hreflist>>>\n...\n<<<srclist>>>\n...\nという感じになっている
                    var idx = msg.IndexOf(srclist);
                    var hrefsmsg = msg.Substring(0, idx - 1);
                    var srcsmsg = msg.Substring(0, msg.Length - 1).Substring(idx);
                    var hrefs = hrefsmsg.Split('\n').Skip(1).ToList();
                    var srcs = srcsmsg.Split('\n').Skip(1).ToList();

                    //MessageBox.Show("href count:" + hrefs.Count + "\n" + "srcs count:" + srcs.Count);

                    //巡回リスト作成
                    CheckAndAddPatrolURLList(hrefs);
                    //MessageBox.Show(string.Join('\n', vm.PatrolURLList));

                    //ダウンロードリスト作成
                    AddDownloadList(srcs);
                } else {
                    MessageBox.Show(msg);
                }
            } catch { } finally { vm.Ready = true; }
        }

        private void Button_Click(object sender, RoutedEventArgs e) {
            Navigate(vm.URL);
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter && vm.Ready)
                Navigate(vm.URL);
        }

        private void Window_Closing(object sender, CancelEventArgs e) {
            var xs = new XmlSerializer(typeof(ViewModel));
            using (var fs = new FileStream(SettingPath, FileMode.Create, FileAccess.Write)) {
                xs.Serialize(fs, vm);
            }
        }

        private bool Navigate(string url) {
            if (!vm.Ready) return false;
            Dispatcher.Invoke(() => {
                if (vm.FirstPatrolURL == null) vm.FirstPatrolURL = url;
                if (vm.URL != url) vm.URL = url;
                webView2.CoreWebView2.Navigate(vm.URL);
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
            string host = new Uri(vm.FirstPatrolURL).Host.ToLower();
            var baseUri = new Uri(vm.URL);
            var addURLs = new List<string>();//後でまとめてInsertするための入れ物
            foreach (var u in urls) {
                bool issrc = false;
                var url = u;
                if (url.StartsWith(src)) {
                    issrc = true;
                    url = url.Substring(src.Length);
                }
                if (url.Contains("#")) continue;//Page内リンクはNavigatedイベントが発生しないので無視（id記法の場合はそもそも取得しない）

                var otherUri = new Uri(baseUri, url);
                if (otherUri.Host.ToLower() != host) continue;//ホストが違う場合巡回しない
                //otherUriがルートを示していれば巡回しない
                if (otherUri.AbsoluteUri.ToLower() == (baseUri.Scheme + "://" + baseUri.Host + "/").ToLower()) continue;

                var check = true;
                if (!issrc) {//a要素がsrcを持つ要素の先祖で有る場合基本的に追加する（SourceDownloaderなので。）
                    check = CheckConditions(otherUri.AbsoluteUri, vm.PatrolConditionList);
                }
                //無視もせず、巡回したリストにも、巡回するリストにも無い場合追加
                if (check && vm.FirstPatrolURL.ToLower()!=otherUri.AbsoluteUri.ToLower() && !vm.PatrolURLList.Contains(otherUri.AbsoluteUri)) {
                    //Appendすると、関係ないページの後にPatrolする事になるので、現在見ているページの直後に追加する
                    //vm.PatrolURLList.Add(otherUri.AbsoluteUri);
                    addURLs.Add(otherUri.AbsoluteUri);
                }
            }
            if (addURLs.Count > 0) {
                var idx = vm.PatrolURLList.IndexOf(vm.URL);
                if (idx < 0)
                    vm.PatrolURLList.AddRange(addURLs);
                else
                    vm.PatrolURLList.InsertRange(idx + 1, addURLs.Distinct());
            }
        }

        public void AddDownloadList(List<string> urls) {
            var baseUri = new Uri(vm.URL);
            foreach (var url in urls) {
                var otherUri = new Uri(baseUri, url);
                if (CheckConditions(otherUri.AbsoluteUri, vm.DownloadConditionList) && !vm.DownloadList.Contains(otherUri.AbsoluteUri))
                    vm.DownloadList.Add(otherUri.AbsoluteUri);
            }
        }
        private bool CheckConditions(string absoluteUri, List<string> conditionList) {
            var check = true;
            foreach (var cnd in conditionList) {
                //!で始まる場合はそれ以降の文字がマッチする場合falseを返す
                //それ以外は、その文字列がマッチしない場合falseを返す
                if (cnd.StartsWith('!')) {
                    if (Regex.IsMatch(absoluteUri, cnd.Substring(1))){
                        check = false;
                        break;
                    }
                } else {
                    //!が付いていない場合は、そのパターンがマッチしないとダメだが、|で区切る事で複数パターン登録できる
                    //登録されている場合、マッチしてもしなくても終了
                    var check2 = false;
                    foreach (var p in cnd.Split('|')) {
                        if (Regex.IsMatch(absoluteUri, cnd)) {
                            check2 = true;
                            break;
                        }
                    }
                    check = check2;
                    break;
                }
            }
            return check;
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
                    using (var ms = new MemoryStream(Convert.FromBase64String(data))) {
                        var bmp = Bitmap.FromStream(ms);
                        bmp.Save(path);
                    }
                } catch { }
            } else {
                try {
                    Uri uri = new Uri(src);
                    var path = GetSavePath(uri.LocalPath);
                    using (var wc = new WebClient()) {
                        wc.DownloadFile(src, path);
                    }
                } catch(WebException ex) {
                    //404 Not found の時無限ループしちゃうのでその対応
                    if (!ex.Message.Contains("404")) throw;
                }
            }
        }
        private string GetSavePath(string name) {
            if (name.StartsWith("/")) name = name.Substring(1);//nameの先頭が/だとルート相対パスになるので削除
            if (name.Contains("/")) name = name.Replace("/", "\\");//やらなくても大丈夫だけど一応置換しとく
            var path = System.IO.Path.Combine(dlDir, name);
            if (File.Exists(path)) {
                var n = name.Split('.')[0];
                var e = name.Split('.')[1];
                var i = 0;
                while (File.Exists(path)) {
                    path = System.IO.Path.Combine(dlDir, n + (++i) + "." + e);
                }
            }
            var dir = new FileInfo(path).DirectoryName;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return path;
        }

        #region statics
        static string onload = "<<<onload>>>";
        static string hreflist = "<<<hreflist>>>";
        static string srclist = "<<<srclist>>>";
        static string src = "<<<src>>>";//a要素の子孫にsrc属性を持つ要素が存在する場合に追加するやつ（ホストが同じなら巡回する）
        //iframeのsrcは巡回する必要があるのでhrefとして追加し、ダウンロード用のsrcからは省く（何が入ってるか不明なのでsrcを含むa要素と同様に確実に巡回するようにする）
        static string GetHrefSrcListFunction {
            get {
                return "function getHrefSrcList(){"
                            + "var as=document.getElementsByTagName('a');"
                            + "var hrefs='" + hreflist + "\\n';"
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
                        + "function getDescendantsSrcValues(ele){"
                            + "var ret='';"
                            + "if(!ele.hasChildNodes())return ret;"
                            + "var cs=ele.childNodes;"
                            + "for(var i=0;i<cs.length;i++){"
                                + "if(cs[i].nodeType!=1) continue;"
                                + "if(cs[i].hasAttribute('src') && cs[i].tagName.toLowerCase()!='iframe'){ ret+=cs[i].getAttribute('src')+'\\n';"
                                + "if(cs[i].getAttribute('src').includes('php'))alert(cs[i].tagName);}"
                                + "ret+=getDescendantsSrcValues(cs[i]);"
                            + "}"
                            + "return ret;"
                        + "}";
            }
        }
        static string OnLoadEvent {
            get {
                return "window.onload = function() {window.chrome.webview.postMessage('<<<onload>>>');}";
            }
        }
        static string GetHTML {
            get {
                return "function getHtml(){"
                        + "window.chrome.webview.postMessage(document.getElementsByTagName('html')[0].outerHTML);"
                        + "}";
            }
        }
        #endregion
    }
    public class ViewModel : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
        [XmlIgnore]
        public string URL {
            get { return _URL; }
            set {
                _URL = value;
                OnPropertyChanged(nameof(URL));
            }
        }
        string _PatrolConditions = "";
        public string PatrolConditions {
            get {
                return _PatrolConditions;
            }
            set {
                _PatrolConditions = value;
                PatrolConditionList = _PatrolConditions.Split('/').Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToList();
            }
        }
        string _DownloadConditions = @"!\.js";
        public string DownloadConditions {
            get {
                DownloadConditionList = _DownloadConditions.Split('/').Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToList();
                return _DownloadConditions;
            }
            set {
                _DownloadConditions = value;
                DownloadConditionList = _DownloadConditions.Split('/').Select(c => c.Trim()).Where(c => !string.IsNullOrEmpty(c)).ToList();
            }
        }
        internal List<string> PatrolConditionList = new List<string>();
        internal List<string> DownloadConditionList = new List<string>();

        public string FirstPatrolURL { get; set; }//ユーザーが最初にNavigateしたURL
        public int PatrolPos { get; set; }//PatrolURLListの何番目を巡回中か
        public List<string> PatrolURLList { get; set; } = new List<string>();//巡回するURLのリスト
        public int DownloadPos { get; set; }//DownloadListの何番目をダウンロード中か
        public List<string> DownloadList { get; set; } = new List<string>();//ダウンロードするファイルのリスト
    }

}
