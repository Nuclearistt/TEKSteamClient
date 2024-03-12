# TEK Steam Client
[![Discord](https://img.shields.io/discord/937821572285206659?style=flat-square&label=Discord&logo=discord&logoColor=white&color=7289DA)]([![Discord](https://img.shields.io/discord/937821572285206659?style=flat-square&label=Discord&logo=discord&logoColor=white&color=7289DA)](https://discord.gg/JBUgcwvpfc))
[![NuGet](https://img.shields.io/nuget/v/TEKSteamClient?style=flat-square&label=NuGet)](https://nuget.org/packages/TEKSteamClient)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/Nuclearistt/TEKSteamClient/build.yaml?style=flat-square)](https://github.com/Nuclearistt/TEKSteamClient/actions/workflows/build.yaml)

## Overview

TEK Steam Client is a fast Steam client implementation that allows installing, updating and validating any app by giving you ability to provide depot decryption keys and manifest request codes from external sources if app is not owned on the account

## Features

+ CM client that allows logging in with any Steam account or using anonymous account, getting depot decryption keys, latest manifest IDs, manifest request codes (custom methods for getting those may be provided if you know that account you're logged in doesn't have access to them), and workshop item details
+ CDN client that can download any manifest or patch, which are then converted to custom file format using tree structure that allows many optimizations
+ AppManager class that provides convenient way to manage Steam apps and update or validate specific depots or workshop items
+ Thread-based chunk downloading that uses LZMA decoder optimized for it and allows you to customize scale based on available CPU and network resources
+ Optimized delta patching and chunk relocation algorithm that works MUCH faster than Steam's own while using MUCH less disk space
+ Update/validation process can be paused at any step with cancellation token with no progress loss

## How to get depot decryption keys and manifest request codes

To download a Steam app you need 2 things: its depot decryption key(s) and a source for manifest request codes.

Depot decryption keys are never updated and you only need to get them once, either by using GetDepotDecryptionKey method of a CMClient logged into an account that owns the app (anonymous will work for free apps), or from `Steam\config\config.vdf` file in Steam installation that downloaded the depot at least once, at "InstallConfigStore" > "Software" > "Valve" > "Steam" > "depots", use Convert.FromHexString() on the "DecryptionKey" entry values to get binary representations that you can use.

Manifest request codes are regularly updated and you need to maintain a CMClient logged into account that owns the app to get them. CDNClient/AppManager will automatically try to get them using their CMClient, but you may use CMClient.ManifestRequestCodeSourceOverrides to forward requests for specific depots elsewhere, for example [MRCP](https://github.com/Nuclearistt/MRCP), so you can process these requests securely on a remote server without exposing your account credentials to client side

## Use example

```cs
using TEKSteamClient;

CDNClient.DepotDecryptionKeys.Add(346111,
[
    0x5E, 0xDB, 0x30, 0x79, 0x72, 0xCD, 0x5B, 0xF1, 0xE3, 0x08, 0x1A, 0xED, 0xC9, 0x86, 0xEF, 0x72,
    0x1D, 0xFD, 0x27, 0xCA, 0xE1, 0x6D, 0x0A, 0x97, 0x6C, 0x6B, 0x7E, 0xA6, 0xE8, 0xFF, 0x20, 0x89
]); //Add decryption keys for depots you are planning to use, they are never updated so you only need to get them once
//Depot 346111 doesn't requre account to own the app to get manifest request codes, but most other paid apps' depots do, see https://github.com/Nuclearistt/MRCP for CMClient.ManifestRequestCodeSourceOverrides example
var appManager = new AppManager(346110, @"C:\Games\ARK Survival Evolved"); //Create AppManager instance for desired app
appManager.StatusUpdated += (newStatus) => Console.WriteLine($"Status updated: {newStatus}"); //Subscribe to manager's events if you want to handle them, there are also ProgressInitiated, ProgressUpdated and ValidationCounterUpdated
bool alreadyUpToDate = appManager.Update(new(346111), CancellationToken.None); //This will update depot 346111 (base game) to latest version or just return true if it's already up to date. On first run it'll instead perform validation since TEK Steam Client doesn't know current installed version. If depot is not installed at all, validation will mark the entire manifest to be downloaded, effectively installing the depot from scratch
appManager.CmClient.Disconnect(); //Disconnect AppManager's CM client so it doesn't keep its thread running around and prevent process exit
```

## License

TEK Steam Client is licensed under the [MIT](https://github.com/Nuclearistt/TEKSteamClient/blob/main/LICENSE) license.