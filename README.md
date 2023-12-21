# TEK Steam Client
[![Discord](https://img.shields.io/discord/937821572285206659?style=flat-square&label=Discord&logo=discord&logoColor=white&color=7289DA)](https://discord.com/servers/teknology-hub-937821572285206659)
[![NuGet](https://img.shields.io/nuget/v/TEKSteamClient?style=flat-square&label=NuGet)](https://nuget.org/packages/TEKSteamClient)

## Overview

TEK Steam Client is a fast Steam client implementation that allows installing, updating and validating any app by giving you ability to provide depot decryption keys and manifest request codes from external sources if app is not owned on the account

## Features

+ CM client that allows logging in with any Steam account or using anonymous account, getting depot decryption keys, latest manifest IDs, manifest request codes (custom methods for getting those may be provided if you know that account you're logged in doesn't have access to them), and workshop item details
+ CDN client that can download any manifest or patch, which are then converted to custom file format using tree structure that allows many optimizations
+ AppManager class that provides convenient way to manage Steam apps and update or validate specific depots or workshop items
+ Task-based chunk downloading that uses LZMA decoder optimized for it and allows you to customize scale based on available CPU and network resources
+ Optimized delta patching and chunk relocation algorithm that works MUCH faster than Steam's own while using MUCH less disk space
+ Update/validation process can be paused at any step with cancellation token with no progress loss

## Use example

```cs
using TEKSteamClient;

CDNClient.DepotDecryptionKeys.Add(346111,
[
    0x5E, 0xDB, 0x30, 0x79, 0x72, 0xCD, 0x5B, 0xF1, 0xE3, 0x08, 0x1A, 0xED, 0xC9, 0x86, 0xEF, 0x72,
    0x1D, 0xFD, 0x27, 0xCA, 0xE1, 0x6D, 0x0A, 0x97, 0x6C, 0x6B, 0x7E, 0xA6, 0xE8, 0xFF, 0x20, 0x89
]); //Add decryption keys for depots you are planning to use, they are never updated so you only need to get them once
var appManager = new AppManager(346110, @"C:\Games\ARK Survival Evolved"); //Create AppManager instance for desired app
appManager.StatusUpdated += (newStatus) => Console.WriteLine($"Status updated: {newStatus}"); //Subscribe to manager's events if you want to handle them, there are also ProgressInitiated, ProgressUpdated and ValidationCounterUpdated
bool alreadyUpToDate = appManager.Update(new(346111), CancellationToken.None); //This will update depot 346111 (base game) to latest version or just return true if it's already up to date. On first run it'll instead perform validation since TEK Steam Client doesn't know current installed version. If depot is not installed at all, validation will mark the entire manifest to be downloaded, effectively installing the depot from scratch
```

## License

TEK Steam Client is licensed under the [MIT](LICENSE) license.