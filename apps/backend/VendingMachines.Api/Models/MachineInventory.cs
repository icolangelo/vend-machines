namespace VendingMachines.Api.Models;

public class MachineInventory
{
    // Hardware
    public string Id { get; set; } = string.Empty;
    public int GrupoId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string Modelo { get; set; } = string.Empty;
    public string VendingMachineId { get; set; } = string.Empty;
    public string FirmwareVersion { get; set; } = string.Empty;
    public bool UseGsm { get; set; }
    public bool UseWifi { get; set; }
    public bool UseEthernet { get; set; }
    public string ConnectionType { get; set; } = string.Empty;
    public bool Ativo { get; set; }
    public bool IsAutoConnectEnabled { get; set; }
    public bool IsLocationEnabled { get; set; }

    // Rede: GSM
    public string SimPin { get; set; } = string.Empty;
    public string GprsApn { get; set; } = string.Empty;
    public string GprsUsername { get; set; } = string.Empty;
    public string GprsPassword { get; set; } = string.Empty;

    // Rede: Wi-Fi
    public string WifiSsid { get; set; } = string.Empty;
    public string WifiPassword { get; set; } = string.Empty;

    // Rede: IP
    public string IpAddress { get; set; } = string.Empty;
    public string IpMask { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public bool IsDhcpEnabled { get; set; }

    // Servidor
    public string ServerUrl { get; set; } = string.Empty;
    public int ServerPort { get; set; }
    public bool UseHttps { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public int WebQueryIntervalMs { get; set; }

    // Vending
    public string EvaSecurityPassword { get; set; } = string.Empty;
    public string EvaPasscode { get; set; } = string.Empty;
    public string EvaBaudRateOption { get; set; } = "0"; // 0 = 9600 bps, 1 = 19200 bps
    public bool EvaLowSpeedStartup { get; set; }

    // FTP
    public bool IsFtpEnabled { get; set; }
    public string FtpServerAddress { get; set; } = string.Empty;
    public int FtpServerPort { get; set; } = 21;
    public string FtpUsername { get; set; } = string.Empty;
    public string FtpPassword { get; set; } = string.Empty;
    public string FtpDirectoryPath { get; set; } = "/evadts/";
    
    // Config Especifica Dinamica
    public string CustomConfigJson { get; set; } = "{}";
}
