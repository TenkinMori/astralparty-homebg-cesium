using CesiumLoader.SDK.Manifests;

[assembly: ModManifest(
    "自定义主页背景",
    "1.2.2",
    "TenkinMori",
    "把游戏主页背景换成自定义视频或图片（mods/HomeBackgroundMod/bg.mp4，缺省 assets/bg.png）。",
    Permissions = ModPermission.ReadGameState | ModPermission.ModifyGameState | ModPermission.UI | ModPermission.FileSystem,
    SdkVersion = "3.1.1")]
