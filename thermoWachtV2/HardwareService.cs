// HardwareService.cs
// OmenMon referansı alınarak yeniden yazıldı:
// - IVisitor pattern ile doğru LHM update sırası (OmenMon'un UpdateVisitor yaklaşımı)
// - Sensör önceliği: CPU Package > Tdie/Tccd > Core max > Tctl(offset düzeltmeli)
// - Async-safe: GetMetrics() Thread.Pool'da güvenle çağrılabilir
// - WMI BIOS sıcaklık fallback (OmenMon -Bios Temp yaklaşımı)

using LibreHardwareMonitor.Hardware;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;

namespace thermoWachtV2
{
    public class HardwareMetrics
    {
        public float CpuUsage { get; set; }
        public float CpuTemp { get; set; }
        public float GpuUsage { get; set; }
        public float GpuTemp { get; set; }
        public float RamUsage { get; set; }
        public float FanSpeed { get; set; }

        // Kaynak bilgisi — debug için
        public string CpuTempSource { get; set; } = "N/A";
        public string FanSpeedSource { get; set; } = "N/A";
    }

    // OmenMon'dan alınan Visitor pattern:
    // hardware.Update() her donanım için ÖNCE çağrılır,
    // sonra sensörler okunur. Bunu IVisitor ile garantiliyoruz.
    internal sealed class HardwareUpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public class HardwareService : IDisposable
    {
        private readonly Computer _computer;
        private readonly HardwareUpdateVisitor _visitor;
        private readonly PerformanceCounter _cpuCounter;
        private readonly PerformanceCounter _ramCounter;
        private readonly float _totalRamMb;

        // LHM thread-safety için kilit
        private readonly object _lhmLock = new object();

        public HardwareService()
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true
            };
            _computer.Open();
            _visitor = new HardwareUpdateVisitor();

            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
            _cpuCounter.NextValue(); // İlk çağrı hep 0 — ısıtma amaçlı

            using var mc = new ManagementClass("Win32_ComputerSystem");
            foreach (ManagementObject mo in mc.GetInstances())
                _totalRamMb = Convert.ToSingle(mo["TotalPhysicalMemory"]) / (1024f * 1024f);
        }

        // Thread-pool'dan güvenle çağrılabilir (Task.Run içinde)
        public HardwareMetrics GetMetrics()
        {
            var m = new HardwareMetrics();

            // PerformanceCounter — hızlı, thread-safe
            try { m.CpuUsage = _cpuCounter.NextValue(); } catch { }
            try { m.RamUsage = ((_totalRamMb - _ramCounter.NextValue()) / _totalRamMb) * 100f; } catch { }

            lock (_lhmLock)
            {
                // OmenMon Visitor pattern: önce tüm donanımı update et, sonra oku
                _computer.Accept(_visitor);

                ReadCpuTemp(m);
                ReadGpu(m);
                ReadFan(m);
            }

            // LHM CPU sıcaklığı bulamazsa WMI BIOS'a sor (OmenMon -Bios Temp yaklaşımı)
            if (m.CpuTemp < 1f)
            {
                m.CpuTemp = GetBiosOrAcpiTemperature();
                m.CpuTempSource = "WMI/ACPI";
            }

            return m;
        }

        // ─────────────────────────────────────────────────────────────────
        // CPU Sıcaklık Okuma
        // Öncelik (OmenMon'un CPUT/Package önceliğinden ilham alınarak):
        //   1. "CPU Package" veya "Package" sensörü  ← en güvenilir
        //   2. AMD Tdie / Tctl/Tdie / Tccd#X         ← gerçek çekirdek sıcaklığı
        //   3. "Core #X" sensörleri — en yüksek değer
        //   4. "Tctl" (tek başına) — AMD offset: -27°C
        // ─────────────────────────────────────────────────────────────────
        private void ReadCpuTemp(HardwareMetrics m)
        {
            float packageTemp = 0f;
            float tdieTemp = 0f;
            float maxCore = 0f;
            float tctlTemp = 0f;

            foreach (var hw in _hardwareCpu())
            {
                // SubHardware dahil tüm sensörler (OmenMon'da da SubHardware traverse ediliyor)
                foreach (var s in AllSensors(hw))
                {
                    if (s.SensorType != SensorType.Temperature) continue;
                    if (!s.Value.HasValue) continue;

                    float v = s.Value.Value;
                    string n = s.Name;

                    if (v < 1f || v > 110f) continue; // fiziksel olarak imkansız değerleri at

                    if (ContainsCI(n, "Package"))
                    {
                        if (v > packageTemp) packageTemp = v;
                    }
                    else if (ContainsCI(n, "Tdie") || ContainsCI(n, "Tccd"))
                    {
                        if (v > tdieTemp) tdieTemp = v;
                    }
                    else if (StartsWithCI(n, "Core"))
                    {
                        if (v > maxCore) maxCore = v;
                    }
                    else if (n.Equals("Tctl", StringComparison.OrdinalIgnoreCase))
                    {
                        // AMD Ryzen: Tctl, gerçek sıcaklığa +27°C offset ekler
                        tctlTemp = v - 27f;
                    }
                }
            }

            // Öncelik sırasına göre seç
            if (packageTemp > 0f)
            {
                m.CpuTemp = packageTemp;
                m.CpuTempSource = "LHM:Package";
            }
            else if (tdieTemp > 0f)
            {
                m.CpuTemp = tdieTemp;
                m.CpuTempSource = "LHM:Tdie";
            }
            else if (maxCore > 0f)
            {
                m.CpuTemp = maxCore;
                m.CpuTempSource = "LHM:Core";
            }
            else if (tctlTemp > 1f)
            {
                m.CpuTemp = tctlTemp;
                m.CpuTempSource = "LHM:Tctl-offset";
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // GPU Okuma
        // ─────────────────────────────────────────────────────────────────
        private void ReadGpu(HardwareMetrics m)
        {
            foreach (var hw in _computer.Hardware)
            {
                if (!hw.HardwareType.ToString().StartsWith("Gpu", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var s in AllSensors(hw))
                {
                    if (!s.Value.HasValue) continue;

                    if (s.SensorType == SensorType.Temperature)
                    {
                        // "GPU Core" veya ilk sıcaklık
                        if (ContainsCI(s.Name, "Core") || m.GpuTemp == 0f)
                            m.GpuTemp = s.Value.Value;
                    }
                    else if (s.SensorType == SensorType.Load)
                    {
                        if (ContainsCI(s.Name, "Core") || ContainsCI(s.Name, "Usage") || ContainsCI(s.Name, "3D"))
                            m.GpuUsage = s.Value.Value;
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // Fan Okuma — tüm donanımlardan (özyinelemeli) en yüksek RPM değeri
        // ─────────────────────────────────────────────────────────────────
        private void ReadFan(HardwareMetrics m)
        {
            foreach (var hw in _computer.Hardware)
            {
                foreach (var s in AllSensors(hw))
                {
                    if (s.SensorType != SensorType.Fan) continue;
                    if (!s.Value.HasValue || s.Value.Value <= 0) continue;
                    if (s.Value.Value > m.FanSpeed)
                    {
                        m.FanSpeed = s.Value.Value;
                        m.FanSpeedSource = $"{hw.Name} / {s.Name}";
                    }
                }
            }

            if (m.FanSpeed < 1f)
            {
                m.FanSpeed = GetWmiFanSpeed();
                if (m.FanSpeed > 0) m.FanSpeedSource = "WMI";
            }
        }

        // ─────────────────────────────────────────────────────────────────
        // WMI / ACPI sıcaklık — OmenMon'un -Bios Temp fallback'ine karşılık gelir
        // Sırasıyla: MSAcpi_ThermalZoneTemperature → Win32_TemperatureProbe
        // ─────────────────────────────────────────────────────────────────
        private static float GetBiosOrAcpiTemperature()
        {
            // 1. ACPI Thermal Zone (en yaygın)
            try
            {
                using var s = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject o in s.Get())
                {
                    float t = (Convert.ToSingle(o["CurrentTemperature"]) / 10f) - 273.15f;
                    if (t > 20f && t < 100f) return t;
                }
            }
            catch { }

            // 2. SMBIOS Temperature Probe (nadiren çalışır ama deneyelim)
            try
            {
                using var s = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_TemperatureProbe");
                foreach (ManagementObject o in s.Get())
                {
                    if (o["CurrentReading"] != null)
                    {
                        float t = Convert.ToSingle(o["CurrentReading"]) / 10f;
                        if (t > 20f && t < 100f) return t;
                    }
                }
            }
            catch { }

            return 0f;
        }

        // Lenovo WMI fan hızı — LNVNB serisi dahil çoğu Lenovo laptopte çalışır
        private static float GetWmiFanSpeed()
        {
            // Yöntem 1: Lenovo özel WMI arayüzü (root\WMI \ LENOVO_FAN_METHOD)
            try
            {
                var scope = new ManagementScope(@"\\.\root\WMI");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT * FROM LENOVO_FAN_METHOD"));
                foreach (ManagementObject obj in searcher.Get())
                {
                    // Fan 0 (CPU fanı)
                    var r0 = obj.InvokeMethod("Fan_GetCurrentFanSpeed", new object[] { 0 });
                    if (r0 != null && float.TryParse(r0.ToString(), out float rpm0) && rpm0 > 0)
                        return rpm0;
                }
            }
            catch { }

            // Yöntem 2: Win32_Fan (standart WMI, genellikle boş döner)
            try
            {
                using var s = new ManagementObjectSearcher("SELECT * FROM Win32_Fan");
                foreach (ManagementObject o in s.Get())
                    if (o["DesiredSpeed"] != null &&
                        float.TryParse(o["DesiredSpeed"].ToString(), out float spd) && spd > 0)
                        return spd;
            }
            catch { }

            return 0f;
        }

        // ─── Yardımcılar ────────────────────────────────────────────────

        // Sadece _computer.Hardware içinden CPU olanları döndürür
        private IEnumerable<IHardware> _hardwareCpu()
        {
            foreach (var hw in _computer.Hardware)
                if (hw.HardwareType == HardwareType.Cpu)
                    yield return hw;
        }

        // Bir donanımın kendi + tüm SubHardware sensörlerini özyinelemeli döndürür
        private static IEnumerable<ISensor> AllSensors(IHardware hw)
        {
            foreach (var s in hw.Sensors) yield return s;
            foreach (var sub in hw.SubHardware)
                foreach (var s in AllSensors(sub)) yield return s;
        }

        private static bool ContainsCI(string src, string value)
            => src.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool StartsWithCI(string src, string value)
            => src.StartsWith(value, StringComparison.OrdinalIgnoreCase);

        // DEBUG: Tüm sensörleri konsola döker — fan sorunu teşhisi için
        public string DumpAllSensors()
        {
            lock (_lhmLock)
            {
                _computer.Accept(_visitor);
                var sb = new System.Text.StringBuilder();
                foreach (var hw in _computer.Hardware)
                    DumpHardware(hw, sb, 0);
                return sb.ToString();
            }
        }

        private static void DumpHardware(IHardware hw, System.Text.StringBuilder sb, int depth)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}[{hw.HardwareType}] {hw.Name}");
            foreach (var s in hw.Sensors)
                sb.AppendLine($"{new string(' ', depth * 2 + 2)}{s.SensorType,-14} | {s.Name,-30} | {s.Value}");
            foreach (var sub in hw.SubHardware)
                DumpHardware(sub, sb, depth + 1);
        }

        public void Dispose()
        {
            _computer.Close();
            _cpuCounter?.Dispose();
            _ramCounter?.Dispose();
        }
    }
}