using Microsoft.Win32;
using System.Linq;
using System.Text;

namespace Lyrictified.Services;

internal sealed class VmDetectionResult
{
    public bool IsVm { get; set; }
    public string BiosManufacturer { get; set; } = string.Empty;
    public string BiosProduct { get; set; } = string.Empty;
    public string BiosManufacturerMatch { get; set; } = string.Empty;
    public string BiosProductMatch { get; set; } = string.Empty;
    public string SystemInfoManufacturer { get; set; } = string.Empty;
    public string SystemInfoModel { get; set; } = string.Empty;
    public string SystemInfoManufacturerMatch { get; set; } = string.Empty;
    public string SystemInfoModelMatch { get; set; } = string.Empty;
    public bool HasVirtualBoxGuestAdditions { get; set; }
    public bool HasVmwareTools { get; set; }
    public List<string> PresentVmServices { get; set; } = new();
    public string? Error { get; set; }
}

internal static class VmDetectionService
{
    private static readonly string[] VmManufacturers = new[]
    {
        "vmware, inc.", "innotek gmbh", "xen", "qemu",
        "parallels software international", "bochs", "red hat", "kvm"
    };

    private static readonly string[] VmProducts = new[]
    {
        "vmware virtual platform", "virtualbox", "hvm domu",
        "parallels virtual platform", "kvm", "qemu"
    };

    private static readonly string[] VmServiceNames = new[]
    {
        // VirtualBox Guest Additions — truly guest-only
        "vboxguest", "vboxservice", "vboxmouse", "vboxsf", "vboxvideo",
        // VMware guest-only drivers (vmx86/vmrawdsk/vmusbmouse are also present on hosts with VMware Workstation installed)
        "vmhgfs", "vmmemctl",
        // Xen
        "xenvif", "xennet", "xenbus", "xenvbd", "xensvc",
        // QEMU
        "qemupciserial", "qemu-ga", "qemudev"
    };

    public static bool IsRunningInVirtualMachine()
    {
        return GetVmDetectionResult().IsVm;
    }

    public static VmDetectionResult GetVmDetectionResult()
    {
        var result = new VmDetectionResult();

        try
        {
            // Check BIOS info in registry
            using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (biosKey != null)
            {
                result.BiosManufacturer = (biosKey.GetValue("SystemManufacturer") as string)?.ToLowerInvariant() ?? string.Empty;
                result.BiosProduct = (biosKey.GetValue("SystemProductName") as string)?.ToLowerInvariant() ?? string.Empty;

                result.BiosManufacturerMatch = VmManufacturers.FirstOrDefault(m => result.BiosManufacturer.Contains(m)) ?? string.Empty;
                result.BiosProductMatch = VmProducts.FirstOrDefault(p => result.BiosProduct.Contains(p)) ?? string.Empty;

                if (!string.IsNullOrEmpty(result.BiosManufacturerMatch) || !string.IsNullOrEmpty(result.BiosProductMatch))
                {
                    result.IsVm = true;
                    return result;
                }
            }

            // Check SystemInformation
            using var sysInfoKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SystemInformation");
            if (sysInfoKey != null)
            {
                result.SystemInfoManufacturer = (sysInfoKey.GetValue("ComputerHardwareManufacturer") as string)?.ToLowerInvariant() ?? string.Empty;
                result.SystemInfoModel = (sysInfoKey.GetValue("ComputerHardwareModel") as string)?.ToLowerInvariant() ?? string.Empty;

                result.SystemInfoManufacturerMatch = VmManufacturers.FirstOrDefault(m => result.SystemInfoManufacturer.Contains(m)) ?? string.Empty;
                result.SystemInfoModelMatch = VmProducts.FirstOrDefault(p => result.SystemInfoModel.Contains(p)) ?? string.Empty;

                if (!string.IsNullOrEmpty(result.SystemInfoManufacturerMatch) || !string.IsNullOrEmpty(result.SystemInfoModelMatch))
                {
                    result.IsVm = true;
                    return result;
                }
            }

            // Check for VM-specific software registry keys
            result.HasVirtualBoxGuestAdditions = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Oracle\VirtualBox Guest Additions") != null;
            result.HasVmwareTools = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VMware, Inc.\VMware Tools") != null;

            if (result.HasVirtualBoxGuestAdditions || result.HasVmwareTools)
            {
                result.IsVm = true;
                return result;
            }

            // Check for VM-specific services
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey != null)
            {
                var serviceNames = servicesKey.GetSubKeyNames();
                result.PresentVmServices = VmServiceNames
                    .Where(vmService => serviceNames.Contains(vmService, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (result.PresentVmServices.Count > 0)
                {
                    result.IsVm = true;
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
        }

        return result;
    }

    public static string FormatResult(VmDetectionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Result: {(result.IsVm ? "VM DETECTED" : "No VM detected")}");
        sb.AppendLine();
        sb.AppendLine($"BIOS Manufacturer: {result.BiosManufacturer}");
        sb.AppendLine($"  -> Match: {(string.IsNullOrEmpty(result.BiosManufacturerMatch) ? "None" : result.BiosManufacturerMatch)}");
        sb.AppendLine($"BIOS Product:      {result.BiosProduct}");
        sb.AppendLine($"  -> Match: {(string.IsNullOrEmpty(result.BiosProductMatch) ? "None" : result.BiosProductMatch)}");
        sb.AppendLine();
        sb.AppendLine($"SystemInfo Manufacturer: {result.SystemInfoManufacturer}");
        sb.AppendLine($"  -> Match: {(string.IsNullOrEmpty(result.SystemInfoManufacturerMatch) ? "None" : result.SystemInfoManufacturerMatch)}");
        sb.AppendLine($"SystemInfo Model:        {result.SystemInfoModel}");
        sb.AppendLine($"  -> Match: {(string.IsNullOrEmpty(result.SystemInfoModelMatch) ? "None" : result.SystemInfoModelMatch)}");
        sb.AppendLine();
        sb.AppendLine($"VirtualBox Guest Additions: {(result.HasVirtualBoxGuestAdditions ? "FOUND" : "Not found")}");
        sb.AppendLine($"VMware Tools:               {(result.HasVmwareTools ? "FOUND" : "Not found")}");
        sb.AppendLine();
        sb.AppendLine($"VM Services checked: {VmServiceNames.Length}");
        sb.AppendLine($"VM Services present: {(result.PresentVmServices.Count > 0 ? string.Join(", ", result.PresentVmServices) : "None")}");

        if (!string.IsNullOrEmpty(result.Error))
        {
            sb.AppendLine();
            sb.AppendLine($"Error during detection: {result.Error}");
        }

        return sb.ToString();
    }

    public static void ShowVmWarningNotification()
    {
        try
        {
            var notifyIcon = new System.Windows.Forms.NotifyIcon();

            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(iconPath))
                notifyIcon.Icon = new System.Drawing.Icon(iconPath);
            else
                notifyIcon.Icon = System.Drawing.SystemIcons.Information;

            notifyIcon.Visible = true;
            notifyIcon.Text = "Lyrictified";

            bool clicked = false;

            notifyIcon.BalloonTipClicked += (_, _) =>
            {
                clicked = true;
                ShowVmWarningDialog();
                notifyIcon.Dispose();
            };

            notifyIcon.BalloonTipClosed += (_, _) =>
            {
                if (!clicked)
                    notifyIcon.Dispose();
            };

            notifyIcon.ShowBalloonTip(
                5000,
                "Lyrictified",
                "VM detected. Audio might be glitchy.\nClick to learn more.",
                System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"VM warning notification failed: {ex}");
        }
    }

    public static void ShowVmWarningDialog()
    {
        try
        {
            using var form = new System.Windows.Forms.Form
            {
                Text = "Lyrictified - VM Detected",
                ClientSize = new System.Drawing.Size(540, 260),
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = System.Drawing.Color.FromArgb(22, 27, 34),
            };

            var label = new System.Windows.Forms.Label
            {
                Text = "We detected that you're running a VM (for testing?). Virtual Machines can fall behind and glitch out in audio. To work around this, we suggest you install Spotify both on your host and the VM, open it on both sides, but play the song from the host side instead. This allows Lyrictified to still get the current playing media through Spotify multi-device notification.",
                ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
                BackColor = form.BackColor,
                AutoSize = false,
                Size = new System.Drawing.Size(500, 170),
                Location = new System.Drawing.Point(20, 15),
                Font = new System.Drawing.Font("Segoe UI", 9.5f),
            };
            form.Controls.Add(label);

            var btnOk = new System.Windows.Forms.Button
            {
                Text = "OK",
                DialogResult = System.Windows.Forms.DialogResult.OK,
                Size = new System.Drawing.Size(120, 32),
                Location = new System.Drawing.Point(210, 200),
                ForeColor = System.Drawing.Color.FromArgb(230, 237, 243),
                BackColor = System.Drawing.Color.FromArgb(28, 33, 40),
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
            };
            btnOk.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(48, 54, 61);
            btnOk.Click += (_, _) => form.Close();
            form.Controls.Add(btnOk);
            form.AcceptButton = btnOk;

            form.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Log($"VM warning dialog failed: {ex}");
        }
    }
}
