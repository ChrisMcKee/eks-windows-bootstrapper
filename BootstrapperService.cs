using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EKS_Windows_Bootstrapper;

public partial class BootstrapperService : BackgroundService
{
    private string? vpcCIDRRange;
    private string? subnetCIDRRange;
    private string? excludedSnatCIDRsEnvVar;
    private string? dnsClusterIP;
    private string? apiVersionAuthentication;
    private string? eksPauseImage;
    private string? kubeletExtraArgs;
    private string? kubeProxyExtraArgs;
    private string? cniConfigDir;
    private string? iamAuthenticator;
    private string? eksClusterCACertFile;
    private string? kubelet;
    private string? kubeproxy;
    private string? credentialProviderDir;
    private string? credentialProviderConfig;
    private string? kubeConfigFile;
    private string? kubeletConfigFile;
    private string? serviceHostExe;
    private string? region;
    private string? clusterEndpoint;
    private string? clusterCertificateAuthorityData;
    private string? serviceCIDR;
    private string? privateDnsName;
    private string? subnetMaskBits;
    private string? internalIp;
    private string? eniMACAddress;
    private string? clusterName;
    private string[]? gatewayIpAddresses;
    private const int SERVICE_FAILURE_COUNT_RESET_SEC = 300;
    private const int SERVICE_FAILURE_FIRST_DELAY_MS = 5000;
    private const int SERVICE_FAILURE_SECOND_DELAY_MS = 30000;
    private const int SERVICE_FAILURE_THIRD_DELAY_MS = 60000;
    private readonly ILogger<BootstrapperService> _logger;

    public BootstrapperService(ILogger<BootstrapperService> logger)
    {
        _logger = logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Gathering system configuration...");
        var stopWatch = Stopwatch.StartNew();

        var userData = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        _logger.LogInformation("Waiting for userdata...");
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(1))
        {
            try
            {
                userData = Amazon.Util.EC2InstanceMetadata.UserData;
                if (!string.IsNullOrEmpty(userData))
                {
                    _logger.LogInformation("Userdata received, took {Elapsed} ms", stopwatch.ElapsedMilliseconds);
                    break;
                }
                await Task.Delay(200, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError("An error occurred while retrieving userdata: {ExMessage}, Retrying...", ex.Message);
            }
        }
        stopwatch.Stop();
        _logger.LogInformation("Userdata: {UserData}", userData);
        if (string.IsNullOrEmpty(userData))
        {
            _logger.LogError("Userdata is empty, exiting...");
            return;
        }

        clusterName = ClusterRegex().Match(userData)?.Groups[1]?.Value ?? throw new ArgumentException("Cluster name was not found in userdata, exiting");
        dnsClusterIP = DnsRegex().Match(userData)?.Groups[1]?.Value ?? throw new ArgumentException("DnsClusterIP was not found in userdata, exiting");
        kubeletExtraArgs = KubeletRegex().Match(userData)?.Groups[1]?.Value ?? string.Empty;
        kubeProxyExtraArgs = KubeProxyExtra().Match(userData).Groups[1].Value ?? string.Empty;

        _logger.LogInformation("Extracted parameters: ClusterName: {ClusterName}, DnsClusterIP: {DnsClusterIp}, KubeletExtraArgs: {KubeletExtraArgs}, KubeProxyExtraArgs: {KubeProxyExtraArgs}",
            clusterName, dnsClusterIP, kubeletExtraArgs, kubeProxyExtraArgs);

        var programFilesDirectory = Environment.GetEnvironmentVariable("ProgramFiles") ?? "C:\\Program Files";
        var programDataDirectory = Environment.GetEnvironmentVariable("ProgramData") ?? "C:\\ProgramData";
        var startScript = Environment.GetEnvironmentVariable("EKS_BOOTSTRAPPER_START_SCRIPT") ?? null;
        apiVersionAuthentication = Environment.GetEnvironmentVariable("API_VERSION_AUTHENTICATION") ?? "client.authentication.k8s.io/v1beta1";

        eksPauseImage = Environment.GetEnvironmentVariable("EKS_PAUSE_IMAGE") ?? "amazonaws.com/eks/pause-windows:latest";
        // This program starts with windows services on EC2 to bootstrap a EKS windows node
        // It waits for userdata to become available and then extracts the necessary information to join the EKS cluster from the pwsh command
        // It then prepares the node and starts the kubernetes components.
        var eksBinDir = Path.Combine(programFilesDirectory, "Amazon", "EKS");
        var eksDataDir = Path.Combine(programDataDirectory, "Amazon", "EKS");
        cniConfigDir = Path.Combine(eksDataDir, "cni", "config");
        iamAuthenticator = Path.Combine(eksBinDir, "aws-iam-authenticator.exe");
        eksClusterCACertFile = Path.Combine(eksDataDir, "cluster_ca.crt");

        var kubernetesBinDir = Path.Combine(programFilesDirectory, "kubernetes");
        var kubernetesDataDir = Path.Combine(programDataDirectory, "kubernetes");
        kubelet = Path.Combine(kubernetesBinDir, "kubelet.exe");
        kubeproxy = Path.Combine(kubernetesBinDir, "kube-proxy.exe");
        credentialProviderDir = Path.Combine(eksBinDir, "credential-providers");
        credentialProviderConfig = Path.Combine(eksBinDir, "ecr-credential-provider-config.json");

        // KUBECONFIG environment variable is set by Install-EKSWorkerNode.ps1
        kubeConfigFile = Environment.GetEnvironmentVariable("KUBECONFIG", EnvironmentVariableTarget.Machine) ?? Path.Combine(kubernetesDataDir, "kubeconfig");

        // Kubelet configuration file
        kubeletConfigFile = Path.Combine(kubernetesDataDir, "kubelet-config.json");

        // Service host to host kubelet and kube-proxy
        serviceHostExe = Path.Combine(eksBinDir, "EKS-WindowsServiceHost.exe");

        // User defined environment variables
        excludedSnatCIDRsEnvVar = Environment.GetEnvironmentVariable("EXCLUDED_SNAT_CIDRS", EnvironmentVariableTarget.Machine); // e.g. '172.40.0.0/24,192.168.40.0/24'

        var instanceId = Amazon.Util.EC2InstanceMetadata.InstanceId;
        var ec2Client = new Amazon.EC2.AmazonEC2Client();
        var client = new Amazon.EKS.AmazonEKSClient();

        var clusterTask = client.DescribeClusterAsync(new Amazon.EKS.Model.DescribeClusterRequest
        {
            Name = clusterName
        }, stoppingToken);
        var instanceInfoTask = ec2Client.DescribeInstancesAsync(new Amazon.EC2.Model.DescribeInstancesRequest
        {
            InstanceIds = [instanceId],

        }, stoppingToken);

        region = Amazon.Util.EC2InstanceMetadata.Region.SystemName;
        eniMACAddress = Amazon.Util.EC2InstanceMetadata.GetData("/mac");
        vpcCIDRRange = Amazon.Util.EC2InstanceMetadata.GetData($"/network/interfaces/macs/{eniMACAddress}/vpc-ipv4-cidr-block");
        subnetCIDRRange = Amazon.Util.EC2InstanceMetadata.GetData($"/network/interfaces/macs/{eniMACAddress}/subnet-ipv4-cidr-block");
        subnetMaskBits = subnetCIDRRange.Split("/", 2)[1];
        internalIp = Amazon.Util.EC2InstanceMetadata.PrivateIpAddress;

        var cluster = await clusterTask;
        clusterEndpoint = cluster.Cluster.Endpoint;
        clusterCertificateAuthorityData = cluster.Cluster.CertificateAuthority.Data;
        serviceCIDR = cluster.Cluster.KubernetesNetworkConfig.ServiceIpv4Cidr ?? "10.100.0.0/16";
        gatewayIpAddresses = GetGatewayIpAddresses().ToArray();
        var instanceInfo = await instanceInfoTask;
        privateDnsName = instanceInfo.Reservations[0].Instances[0].PrivateDnsName;

        stopWatch.Stop();
        _logger.LogInformation("Gathered system configuration in {ElapsedMilliseconds} ms", stopWatch.ElapsedMilliseconds);
        stopWatch.Reset();

        _logger.LogInformation("Configuring EKS Windows Node");
        stopWatch.Start();
        await Task.WhenAll(
            ConfigureHNS(),
            startScript == null ? StartService("containerd") : ExecutePowershellScript(startScript),
            UpdateKubeConfig(),
            UpdateEksCniConfig(),
            UpdateKubeletConfig(),
            RegisterKubernetesServices(),
            GenerateResolvConf()
        );
        await Task.WhenAll(
            StartService("kubelet"),
            StartService("kube-proxy")
        );

        stopWatch.Stop();
        _logger.LogInformation("EKS Windows Node Configured in {StopWatchElapsedMilliseconds} ms", stopWatch.ElapsedMilliseconds);
    }


    private static IEnumerable<string> GetGatewayIpAddresses()
    {
        var netRoutes = NetworkInterface.GetAllNetworkInterfaces();
        foreach (var netRoute in netRoutes)
        {
            var ipProperties = netRoute.GetIPProperties();
            var gateways = ipProperties.GatewayAddresses;
            foreach (var gateway in gateways)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return gateway.Address.ToString();
                }
            }
        }
    }

    private List<string> GetCombinedSNATExclusionList()
    {
        if (string.IsNullOrEmpty(vpcCIDRRange))
        {
            throw new ArgumentNullException("VpcCIDRRange");
        }
        List<string> combinedCIDRRange = [vpcCIDRRange];
        if (!string.IsNullOrEmpty(excludedSnatCIDRsEnvVar))
        {
            _logger.LogInformation("Excluding environment variable specified CIDR ranges for SNAT in CNI config");
            combinedCIDRRange.AddRange(excludedSnatCIDRsEnvVar.Split(","));
        }

        return combinedCIDRRange;
    }

    private async Task UpdateKubeConfig()
    {
        if (string.IsNullOrEmpty(clusterCertificateAuthorityData))
        {
            throw new ArgumentNullException(nameof(clusterCertificateAuthorityData));
        }
        if (string.IsNullOrEmpty(eksClusterCACertFile))
        {
            throw new ArgumentNullException(nameof(eksClusterCACertFile));
        }
        if (string.IsNullOrEmpty(kubeConfigFile))
        {
            throw new ArgumentNullException(nameof(kubeConfigFile));
        }
        var caFileWriteTask = File.WriteAllBytesAsync(eksClusterCACertFile, Convert.FromBase64String(clusterCertificateAuthorityData));
        var kubeConfig = $"""

                              apiVersion: v1
                              kind: Config
                              clusters:
                              - cluster:
                                  certificate-authority: {eksClusterCACertFile}
                                  server: {clusterEndpoint}
                                name: kubernetes
                              contexts:
                              - context:
                                  cluster: kubernetes
                                  user: kubelet
                                name: kubelet
                              current-context: kubelet
                              users:
                              - name: kubelet
                                user:
                                  exec:
                                    apiVersion: {apiVersionAuthentication}
                                    command: {iamAuthenticator}
                                    args:
                                      - "token"
                                      - "-i"
                                      - "{clusterName}"
                                      - --region
                                      - "{region}"

                          """;

        await Task.WhenAll(
            File.WriteAllTextAsync(kubeConfigFile, kubeConfig, Encoding.ASCII),
            caFileWriteTask
        );
    }

    private async Task UpdateEksCniConfig()
    {
        var CNIConfigFile = $"{cniConfigDir}\\vpc-bridge.conf";
        var SNATExcludedCIDRs = GetCombinedSNATExclusionList();
        var dnsSuffixList = new[] { "{%namespace%}.svc.cluster.local", "svc.cluster.local", "cluster.local" };
        const string cniSpecVersion = "0.4.0";
        var additionalCNIConf = """
                                        "disableCheck": true,
                                """;

        var CNIConfig = $$"""
                              {
                                  "cniVersion": "{{cniSpecVersion}}",
                                  "name": "vpc",
                                  "type": "vpc-bridge",
                                  "capabilities": {"portMappings": true},{{additionalCNIConf}}
                                  "eniMACAddress": "{{eniMACAddress}}",
                                  "eniIPAddresses": ["{{internalIp}}/{{subnetMaskBits}}"],
                                  "gatewayIPAddress": "{{gatewayIpAddresses?.FirstOrDefault()}}",
                                  "vpcCIDRs": [{{string.Join(',', SNATExcludedCIDRs.Select(cidr => $"\"{cidr}\""))}}],
                                  "serviceCIDR": "{{serviceCIDR}}",
                                  "dns": {
                                      "nameservers": ["{{dnsClusterIP}}"],
                                      "search": [{{string.Join(',', dnsSuffixList.Select(suffix => $"\"{suffix}\""))}}]
                                  }
                              }
                          """;

        await File.WriteAllTextAsync(CNIConfigFile, CNIConfig, Encoding.ASCII);
    }

    private async Task UpdateKubeletConfig()
    {
        if (string.IsNullOrEmpty(kubeletConfigFile))
        {
            throw new ArgumentNullException(nameof(kubeletConfigFile));
        }
        var kubeletConfig = $$"""
                                  {
                                      "kind": "KubeletConfiguration",
                                      "apiVersion": "kubelet.config.k8s.io/v1beta1",
                                      "address": "0.0.0.0",
                                      "authentication": {
                                          "anonymous": {
                                              "enabled": false
                                          },
                                          "webhook": {
                                              "cacheTTL": "2m0s",
                                              "enabled": true
                                          },
                                          "x509": {
                                              "clientCAFile": "{{eksClusterCACertFile?.Replace("\\", @"\\")}}"
                                          }
                                      },
                                      "authorization": {
                                          "mode": "Webhook",
                                          "webhook": {
                                              "cacheAuthorizedTTL": "5m0s",
                                              "cacheUnauthorizedTTL": "30s"
                                          }
                                      },
                                      "clusterDomain": "cluster.local",
                                      "hairpinMode": "hairpin-veth",
                                      "cgroupDriver": "cgroupfs",
                                      "cgroupRoot": "/",
                                      "featureGates": {
                                          "RotateKubeletServerCertificate": true
                                      },
                                      "serializeImagePulls": false,
                                      "serverTLSBootstrap": true,
                                      "clusterDNS": [
                                          "{{dnsClusterIP}}"
                                      ]
                                  }
                              """;

        await File.WriteAllTextAsync(kubeletConfigFile, kubeletConfig, Encoding.ASCII);
    }

    private async Task RegisterKubernetesServices()
    {
        var kubeletArgs = new StringBuilder();
        kubeletArgs.Append(" --config=\\\"" + kubeletConfigFile + "\\\"");
        kubeletArgs.Append(" --cloud-provider=external");
        kubeletArgs.Append(" --kubeconfig=\\\"" + kubeConfigFile + "\\\"");
        kubeletArgs.Append(" --hostname-override=" + privateDnsName);
        kubeletArgs.Append(" --v=1");
        kubeletArgs.Append(" --pod-infra-container-image=\\\"" + eksPauseImage + "\\\"");
        kubeletArgs.Append(" --resolv-conf=\\\"\\\"");
        kubeletArgs.Append(" --enable-debugging-handlers");
        kubeletArgs.Append(" --cgroups-per-qos=false");
        kubeletArgs.Append(" --enforce-node-allocatable=\\\"\\\"");
        kubeletArgs.Append(" --container-runtime-endpoint=\\\"npipe:////./pipe/containerd-containerd\\\"");
        kubeletArgs.Append(" --image-credential-provider-bin-dir=\\\"" + credentialProviderDir + "\\\"");
        kubeletArgs.Append(" --image-credential-provider-config=\\\"" + credentialProviderConfig + "\\\"");
        kubeletArgs.Append(" --node-ip=" + internalIp);

        kubeletArgs.Append(" " + kubeletExtraArgs?.Replace("\"", "\\\""));

        // Register the windows service
        const string kubeletServiceName = "kubelet";
        const string kubeProxyServiceName = "kube-proxy";

        var kubeletTask = Task.Run(() =>
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe")
            {
                Arguments = $"create \"{kubeletServiceName}\" binPath= \"\\\"{serviceHostExe}\\\" {kubeletServiceName} \\\"{kubelet}\\\" {kubeletArgs}\" start= demand",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            using var configure = Process.Start("sc.exe", $"failure {kubeletServiceName} reset={SERVICE_FAILURE_COUNT_RESET_SEC} actions=\"restart/{SERVICE_FAILURE_FIRST_DELAY_MS} + /restart/{SERVICE_FAILURE_SECOND_DELAY_MS} + /restart/{SERVICE_FAILURE_THIRD_DELAY_MS}\"");
            configure?.WaitForExit();
            using var failure = Process.Start("sc.exe", $"failureflag {kubeletServiceName} 1");
            failure?.WaitForExit();
        });

        var kubeProxyArgs = string.Join(" ",
            $"--kubeconfig=\\\"{kubeConfigFile}\\\"",
            "--v=1",
            "--proxy-mode=kernelspace",
            $"--hostname-override=\\\"{privateDnsName}\\\"",
            $"--cluster-cidr=\\\"{vpcCIDRRange}\\\"",
            kubeProxyExtraArgs);

        var kubeProxyTask = Task.Run(() =>
        {
            using var process = Process.Start(new ProcessStartInfo("sc.exe")
            {
                Arguments = $"create {kubeProxyServiceName} binPath= \"\\\"{serviceHostExe}\\\" {kubeProxyServiceName} \\\"{kubeproxy}\\\" {kubeProxyArgs}\" start= demand",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            using var configure = Process.Start("sc.exe", $"failure {kubeProxyServiceName} reset={SERVICE_FAILURE_COUNT_RESET_SEC} actions=\"restart/{SERVICE_FAILURE_FIRST_DELAY_MS} + /restart/{SERVICE_FAILURE_SECOND_DELAY_MS} + /restart/{SERVICE_FAILURE_THIRD_DELAY_MS}\"");
            configure?.WaitForExit();
            using var failure = Process.Start("sc.exe", $"failureflag {kubeProxyServiceName} 1");
            failure?.WaitForExit();
        });
        await Task.WhenAll(kubeletTask, kubeProxyTask);
    }

    private async Task GenerateResolvConf()
    {
        string resolvDir = @"c:\etc";
        string resolvFile = Path.Combine(resolvDir, "resolv.conf");

        // Creating resolv dir, if it doesn't exist
        if (!Directory.Exists(resolvDir))
        {
            _logger.LogInformation("Creating resolv directory: {ResolvDir}", resolvDir);
            Directory.CreateDirectory(resolvDir);
        }

        // Getting unique comma separated Dns servers from the Ipv4 network interfaces (AddressFamily 2 represents IPv4)
        string[] dnsServers = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.Supports(NetworkInterfaceComponent.IPv4))
            .SelectMany(ni => ni.GetIPProperties().DnsAddresses)
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => ip.ToString())
            .Distinct()
            .ToArray();

        await File.WriteAllTextAsync(resolvFile, $"nameserver {string.Join(",", dnsServers)}", Encoding.ASCII);
    }

    private async Task ExecutePowershellScript(string filePath)
    {
        var scriptPath = Path.Combine(Environment.CurrentDirectory, filePath);
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -NoLogo -File \"{scriptPath}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process == null) throw new Exception("Failed to start powershell process");
        await process.WaitForExitAsync();
        _logger.LogInformation("Powershell script: {FilePath}", filePath);
        _logger.LogInformation("Powershell script output: {ReadToEnd}", await process.StandardOutput.ReadToEndAsync());
    }

    private static async Task StartService(string serviceName)
    {
        var process = Process.Start("sc.exe", $"start {serviceName}");
        if (process != null)
        {
            await process.WaitForExitAsync();
        }
    }

    private async Task ConfigureHNS()
    {
        if (string.IsNullOrEmpty(eniMACAddress))
        {
            throw new ArgumentNullException("EniMACAddress");
        }

        var vSwitchName = $"vpcbr{eniMACAddress.Replace(":", "")}";
        Environment.SetEnvironmentVariable("KUBE_NETWORK", vSwitchName, EnvironmentVariableTarget.Machine);
        var netobj = new StringBuilder();
        netobj.AppendLine("{");
        netobj.AppendLine("    \"Type\": \"L2Bridge\",");

        if (!string.IsNullOrEmpty(vSwitchName))
        {
            netobj.AppendLine($"    \"Name\": \"{vSwitchName}\",");
        }

        if (!string.IsNullOrEmpty(subnetCIDRRange))
        {
            var prefixes = subnetCIDRRange.Split(',');
            var gateways = gatewayIpAddresses?[0]?.Split(',');

            netobj.AppendLine("    \"Subnets\": [");
            for (int i = 0; i < prefixes.Length; i++)
            {
                netobj.AppendLine("        {");
                netobj.AppendLine($"            \"AddressPrefix\": \"{prefixes[i]}\",");

                if (gateways != null && i < gateways.Length && !string.IsNullOrEmpty(gateways[i]))
                {
                    netobj.AppendLine($"            \"GatewayAddress\": \"{gateways[i]}\"");
                }

                netobj.AppendLine("        }"); // Remove the comma here
                if (i < prefixes.Length - 1)
                {
                    netobj.AppendLine(",");
                }
            }
            netobj.AppendLine("    ]");
        }

        netobj.AppendLine("}");

        var jsonString = netobj.ToString();
        _logger.LogInformation("Creating HNS network object: {JsonString}", jsonString);
        bool success = false;
        string response = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        while (!success && stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            [DllImport("vmcompute.dll")]
            static extern void HNSCall([MarshalAs(UnmanagedType.LPWStr)] string method,
                           [MarshalAs(UnmanagedType.LPWStr)] string path,
                           [MarshalAs(UnmanagedType.LPWStr)] string request,
                           [MarshalAs(UnmanagedType.LPWStr)] out string response);
            HNSCall("POST", "/networks", jsonString, out response);
            _logger.LogInformation("HNS network object creation response: {Response}", response);
            var match = Regex.Match(response, "\"Success\":\\s*(true|false)");
            if (match.Success)
            {
                success = bool.Parse(match.Groups[1].Value);
            }
            if (!success)
            {
                _logger.LogInformation("HNS network object creation failed, Elapsed {ElapsedMilliseconds}ms, Retrying in 1 second...", stopwatch.ElapsedMilliseconds);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        if (!success)
        {
            throw new Exception("Failed to create HNS network object");
        }

        _logger.LogInformation("HNS network object created successfully, Elapsed {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
        _logger.LogInformation("HNS network object creation response: {Response}", response);
        await AddRoutesTovNIC();
    }

    private async Task AddRoutesTovNIC()
    {
        // 169.254.169.254 is for metadata service
        // 169.254.169.250 is for KmsInstanceVpc1
        // 169.254.169.251 is for KmsInstanceVpc2
        // 169.254.169.249 is for G3GridLicense
        // 169.254.169.123 is for AmzTimeSyncServiceVipIp
        // 169.254.169.253 is for DNS server
        string[] ipAddrs = { "169.254.169.254", "169.254.169.250", "169.254.169.251", "169.254.169.249", "169.254.169.123", "169.254.169.253" };
        _logger.LogInformation("Looking for vNIC with Name 'vEthernet*' to add routes");
        var timeout = TimeSpan.FromSeconds(10);
        var interval = TimeSpan.FromMilliseconds(200);
        var stopwatch = Stopwatch.StartNew();
        NetworkInterface? vNIC = null;

        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                vNIC = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(ni => ni.Name.StartsWith("vEthernet"));
                if (vNIC != null) break;
                _logger.LogInformation("vNIC for ENI 'vEthernet*' is not available yet to add routes. Time elapsed: {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
                await Task.Delay(interval);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while looking for vNIC with Name 'vEthernet*' to add routes");
            }
        }

        if (vNIC == null)
        {
            _logger.LogInformation("vNIC for ENI 'vEthernet*' is not available yet to add routes");
            return;
        }

        var vNICIndex = vNIC.GetIPProperties().GetIPv4Properties().Index;

        var routeAddCommands = new StringBuilder();
        for (int i = 0; i < ipAddrs.Length; i++)
        {
            routeAddCommands.Append($"route ADD {ipAddrs[i]} MASK 255.255.255.255 0.0.0.0 IF {vNICIndex}");
            if (i < ipAddrs.Length - 1)
            {
                routeAddCommands.Append(" & ");
            }
        }

        // Execute the route add commands using System.Diagnostics.Process
        var process = new Process();
        process.StartInfo.FileName = "cmd.exe";
        process.StartInfo.Arguments = $"/C {routeAddCommands}";
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        await process.WaitForExitAsync();
        _logger.LogInformation("Added routes to vNIC: {VNicName}", vNIC.Name);
        _logger.LogInformation("Route add commands: {RouteAddCommands}", routeAddCommands);
        _logger.LogInformation("Route add command output: {ReadToEnd}", await process.StandardOutput.ReadToEndAsync());
    }

    [GeneratedRegex("-EKSClusterName '([^']+)'")]
    private static partial Regex ClusterRegex();
    [GeneratedRegex("-DNSClusterIP '([^']+)'")]
    private static partial Regex DnsRegex();
    [GeneratedRegex("-KubeletExtraArgs '([^']+)'")]
    private static partial Regex KubeletRegex();
    [GeneratedRegex("-KubeProxyExtraArgs '([^']+)'")]
    private static partial Regex KubeProxyExtra();
}