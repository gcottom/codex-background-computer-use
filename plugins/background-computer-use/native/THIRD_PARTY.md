# Native helper dependencies

All resolved NuGet versions and NuGet SHA512 content hashes are recorded in `packages.lock.json`. `Build.ps1` requires locked restore. The helper is published as one self-contained win-x64 executable with native libraries bundled for extraction; the executable SHA256 binds its compiled implementation and dependencies.

| Component | Version / source | License |
|---|---|---|
| Microsoft WinApp UIAutomation | 0.7.1; commit `e3cd0b94f3e7b776ac51ba0564f4908094e979b5` | [MIT](https://github.com/microsoft/WinAppCli/blob/v0.7.1/LICENSE) |
| Microsoft.Extensions.DependencyInjection and Logging | 10.0.5 direct; transitive packages pinned in lock file | [MIT](https://github.com/dotnet/runtime/blob/v10.0.5/LICENSE.TXT) |
| .NET / Windows Desktop runtime | Self-contained runtime selected by .NET SDK 10.0.403 and lock file | [.NET MIT and third-party notices](https://github.com/dotnet/runtime/blob/v10.0.14/LICENSE.TXT), [WPF MIT](https://github.com/dotnet/wpf/blob/v10.0.14/LICENSE.TXT) |
| Windows SDK .NET reference projection | Version pinned in lock file | [Microsoft Windows SDK terms](https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref) |

Official portable SDK: `https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip`.

SDK SHA512, verified against [Microsoft release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json):

`7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48`

Official UIAutomation 0.7.1 release NuGet SHA256:

`2c832026b6e11096a1e78dc82bb9295a576414cc57c6155a08539b3f390b58db`

The portable SDK was extracted privately to `D:\Tools\BackgroundComputerUse\dotnet`; its `dotnet.exe` Microsoft Authenticode signature was valid. No PATH, global SDK selection, global tool installation, or certificate trust changes are required. Build environment disables SDK telemetry and automatic HTTPS development certificate generation.

NuGet.org's repository-signed package ZIP differs from the GitHub release ZIP (`eb5c2dfe260e0e058123049281001a698741379f9df1550c7ac653ce1af0e66f` SHA256). Both contained UIAutomation assemblies with identical SHA256: WGC target `7b062661e7c37f1f56bb74202aee6a1689c5dbcb0de973d2aeb48817ef37fa37`; lean target `f04e8e1bcf6a0596864bbaca538a89da4c77469dda306863bf5929a244c85c82`.

