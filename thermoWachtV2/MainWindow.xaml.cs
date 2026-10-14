using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;

namespace thermoWachtV2
{
    public partial class MainWindow : Window
    {
        // ─── Bildirim ayarları ───────────────────────────────────────────
        // Aynı kanal için minimum bekleme süresi (saniye)
        // Örn: CPU sıcaklığı 30 saniyede bir uyarı verir, ama GPU aşılırsa hemen bildirir
        private const int ALERT_COOLDOWN_SECONDS = 30;

        // ─── Donanım servisi ve timer ────────────────────────────────────
        private readonly HardwareService _service;
        private readonly DispatcherTimer _timer;
        private bool _isUpdating = false;

        // ─── Kullanıcının ayarladığı limitler (0 = kapalı) ──────────────
        private float _limitCpuUsage = 0;
        private float _limitCpuTemp = 0;
        private float _limitGpuTemp = 0;
        private float _limitRamUsage = 0;

        // ─── Her kanal için ayrı cooldown takibi ─────────────────────────
        // Bir kanal aşılınca sadece o kanal beklemeye girer, diğerleri etkilenmez
        private readonly Dictionary<string, DateTime> _lastAlertTimes = new()
        {
            { "CpuUsage", DateTime.MinValue },
            { "CpuTemp",  DateTime.MinValue },
            { "GpuTemp",  DateTime.MinValue },
            { "RamUsage", DateTime.MinValue },
        };

        // ─── Constructor ─────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            _service = new HardwareService();
            TrayIcon.Icon = System.Drawing.SystemIcons.Information;
            RegisterAumid();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += async (s, e) => await RunUpdateAsync();
            _timer.Start();

            // DEBUG: Sensör listesini masaüstüne yaz
            Task.Run(() =>
            {
                var dump = _service.DumpAllSensors();
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "sensors_dump.txt"),
                    dump);
            });
        }

        // ─── Ana güncelleme döngüsü ──────────────────────────────────────
        private async Task RunUpdateAsync()
        {
            if (_isUpdating) return;
            _isUpdating = true;

            try
            {
                // Ağır LHM işi arka thread'de, UI bloklanmıyor
                var d = await Task.Run(() => _service.GetMetrics());

                // Ekranı güncelle
                CpuText.Text = $"%{d.CpuUsage:F0}";
                CpuTempText.Text = d.CpuTemp > 0 ? $"{d.CpuTemp:F0}°C" : "N/A";
                GpuTempText.Text = d.GpuTemp > 0 ? $"{d.GpuTemp:F0}°C" : "N/A";
                GpuUsageText.Text = $"%{d.GpuUsage:F0}";
                RamText.Text = $"%{d.RamUsage:F0}";
                FanText.Text = d.FanSpeed > 0 ? $"{d.FanSpeed:F0} RPM" : "N/A";
                FanText.ToolTip = d.FanSpeed > 0 ? d.FanSpeedSource : "Sensör bulunamadı";

                // Limit kontrol et ve gerekirse bildirim gönder
                CheckAndAlert("CpuUsage", _limitCpuUsage, d.CpuUsage,
                    $"CPU Kullanımı: %{d.CpuUsage:F0}  (Limit: %{_limitCpuUsage:F0})");

                CheckAndAlert("CpuTemp", _limitCpuTemp, d.CpuTemp > 0 ? d.CpuTemp : -1,
                    $"CPU Sıcaklığı: {d.CpuTemp:F0}°C  (Limit: {_limitCpuTemp:F0}°C)");

                CheckAndAlert("GpuTemp", _limitGpuTemp, d.GpuTemp,
                    $"GPU Sıcaklığı: {d.GpuTemp:F0}°C  (Limit: {_limitGpuTemp:F0}°C)");

                CheckAndAlert("RamUsage", _limitRamUsage, d.RamUsage,
                    $"RAM Kullanımı: %{d.RamUsage:F0}  (Limit: %{_limitRamUsage:F0})");
            }
            catch { }
            finally
            {
                _isUpdating = false;
            }
        }

        // ─── Limit kontrol mantığı ───────────────────────────────────────
        // channel  : hangi kanal (CpuUsage, CpuTemp, ...)
        // limit    : kullanıcının girdiği limit değeri (0 ise kapalı)
        // current  : anlık okunan değer
        // message  : bildirimde gösterilecek detay metni
        private void CheckAndAlert(string channel, float limit, float current, string message)
        {
            // Limit 0 ise bu kanal devre dışı — hiç kontrol etme
            if (limit <= 0) return;

            // Değer geçersizse (-1 gönderildi = N/A) kontrol etme
            if (current < 0) return;

            // Kullanıcının girdiği değerin ÜSTÜNE çıktı mı?
            // Tam eşit olması yetmez, 1 birim bile aşılınca tetiklenir
            if (current <= limit) return;

            // Bu kanal için cooldown doldu mu?
            if ((DateTime.Now - _lastAlertTimes[channel]).TotalSeconds < ALERT_COOLDOWN_SECONDS)
                return;

            // Cooldown'ı güncelle ve bildirimi gönder
            _lastAlertTimes[channel] = DateTime.Now;
            SendAlert("⚠️ Değer Aşıldı!", message);
        }

        // ─── Bildirim gönderme (WinRT Toast → Tray Balloon fallback) ─────
        private void SendAlert(string title, string body)
        {
            // Yöntem 1: Windows Bildirim Merkezi (sağdan kayan)
            try
            {
                string xml = $@"<toast duration='short'>
                    <visual>
                        <binding template='ToastGeneric'>
                            <text>{Esc(title)}</text>
                            <text>{Esc(body)}</text>
                        </binding>
                    </visual>
                    <audio src='ms-winsoundevent:Notification.Default'/>
                </toast>";

                var doc = new XmlDocument();
                doc.LoadXml(xml);

                ToastNotificationManager
                    .CreateToastNotifier("ThermoWatchV2")
                    .Show(new ToastNotification(doc));

                return; // başarılıysa burada dur
            }
            catch { }

            // Yöntem 2: Tray balloon (her koşulda çalışır)
            try
            {
                TrayIcon.ShowBalloonTip(title, body, BalloonIcon.Warning);
            }
            catch { }
        }

        private static string Esc(string s)
            => System.Security.SecurityElement.Escape(s);

        // ─── Windows bildirim sistemi kaydı ──────────────────────────────
        // Paketlenmemiş masaüstü uygulamalar toast göndermek için
        // registry'ye AUMID kaydetmek zorundadır.
        private static void RegisterAumid()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"SOFTWARE\Classes\AppUserModelId\ThermoWatchV2");
                key.SetValue("DisplayName", "ThermoWatch V2");
                key.SetValue("IconUri", System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch { }
        }

        // ─── Ayarlar sayfası ─────────────────────────────────────────────
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Kaydedilmiş limitleri slider'lara yükle
            CpuSlider.Value = _limitCpuUsage;
            CpuTempSlider.Value = _limitCpuTemp;
            GpuSlider.Value = _limitGpuTemp;
            RamSlider.Value = _limitRamUsage;

            DashboardPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Slider değerlerini limit değişkenlerine kaydet
            _limitCpuUsage = (float)CpuSlider.Value;
            _limitCpuTemp = (float)CpuTempSlider.Value;
            _limitGpuTemp = (float)GpuSlider.Value;
            _limitRamUsage = (float)RamSlider.Value;

            // Kaydedince tüm cooldown'ları sıfırla — yeni limitler hemen aktif
            _lastAlertTimes["CpuUsage"] = DateTime.MinValue;
            _lastAlertTimes["CpuTemp"] = DateTime.MinValue;
            _lastAlertTimes["GpuTemp"] = DateTime.MinValue;
            _lastAlertTimes["RamUsage"] = DateTime.MinValue;

            SettingsPage.Visibility = Visibility.Collapsed;
            DashboardPage.Visibility = Visibility.Visible;

            // Onay bildirimi — sistemin çalışıp çalışmadığını test et
            SendAlert("✅ ThermoWatch V2", "Limitler kaydedildi. İzleme aktif.");
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            DashboardPage.Visibility = Visibility.Visible;
        }

        // ─── Slider etiket güncellemesi ──────────────────────────────────
        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CpuLimitLabel == null) return;
            CpuLimitLabel.Text = CpuSlider.Value == 0 ? "Kapalı" : $"{CpuSlider.Value:F0}%";
            CpuTempLimitLabel.Text = CpuTempSlider.Value == 0 ? "Kapalı" : $"{CpuTempSlider.Value:F0}°C";
            GpuLimitLabel.Text = GpuSlider.Value == 0 ? "Kapalı" : $"{GpuSlider.Value:F0}°C";
            RamLimitLabel.Text = RamSlider.Value == 0 ? "Kapalı" : $"{RamSlider.Value:F0}%";
        }

        // ─── Pencere davranışları ─────────────────────────────────────────
        private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => DragMove();

        private void Hide_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            TrayIcon.ShowBalloonTip("ThermoWatch V2", "Arka planda çalışıyor.", BalloonIcon.Info);
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized) Hide();
        }

        private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e)
        {
            Show(); WindowState = WindowState.Normal; Activate();
        }

        private void MenuShow_Click(object sender, RoutedEventArgs e)
        {
            Show(); WindowState = WindowState.Normal; Activate();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            TrayIcon.Dispose();
            _service.Dispose();
            Application.Current.Shutdown();
        }
    }
}