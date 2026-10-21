using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

// Standalone Quest 3 build of the teleop client. Touches Android-only settings,
// so the PC VR (Standalone) setup and the scene list in Build Settings stay as
// they are.
//
// Editor: menu NurSim > Quest > Configure Android / Build APK.
// Batchmode (Editor closed):
//   Unity.exe -batchmode -quit -projectPath <proj> -buildTarget Android
//     -executeMethod QuestBuild.BuildFromCommandLine -logFile <log>
public static class QuestBuild
{
    const string PackageName = "com.alfakentavr.nursim.teleop";
    const string ApkPath = "Builds/Quest/NurSimTeleop.apk";
    const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";

    static readonly string[] Scenes =
    {
        "Assets/Oculus Control/OculusControl.unity",
        "Assets/Oculus Control/Liminal.unity",
    };

    // OpenXR features enabled for Android; every other Android feature
    // (Vive Focus, debug tools) is turned off.
    static readonly string[] AndroidFeatureTypes =
    {
        "MetaQuestFeature",
        "OculusTouchControllerProfile",
        "MetaQuestTouchPlusControllerProfile",
    };

    [MenuItem("NurSim/Quest/Configure Android")]
    public static void ConfigureAndroid()
    {
        ConfigurePlayerSettings();
        ConfigureXRLoader();
        ConfigureOpenXRFeatures();
        AssetDatabase.SaveAssets();
        Debug.Log("[QuestBuild] Android settings configured.");
    }

    [MenuItem("NurSim/Quest/Build APK")]
    public static void BuildApk()
    {
        if (!Build())
            throw new Exception("[QuestBuild] Build failed, see the log above.");
    }

    public static void BuildFromCommandLine()
    {
        bool succeeded;
        try
        {
            succeeded = Build();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            succeeded = false;
        }
        EditorApplication.Exit(succeeded ? 0 : 1);
    }

    static bool Build()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        ConfigureAndroid();
        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));

        var options = new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };
        BuildReport report = BuildPipeline.BuildPlayer(options);
        BuildSummary summary = report.summary;
        Debug.Log($"[QuestBuild] Result: {summary.result}, size {summary.totalSize / (1024 * 1024)} MB, " +
                  $"{summary.totalErrors} errors, {summary.totalTime.TotalMinutes:F1} min, {ApkPath}");
        return summary.result == BuildResult.Succeeded;
    }

    static void ConfigurePlayerSettings()
    {
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, PackageName);
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Minimal);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        // ROS-TCP and the UDP camera stream need network access.
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
    }

    static void ConfigureXRLoader()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget))
            throw new Exception("[QuestBuild] XR Plug-in Management settings not found.");

        if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

        XRGeneralSettings androidSettings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        androidSettings.InitManagerOnStart = true;
        XRManagerSettings manager = androidSettings.Manager;

        // One runtime on Android: OpenXR. The Oculus XR loader must not also be active.
        foreach (XRLoader loader in manager.activeLoaders.ToList())
        {
            string typeName = loader.GetType().FullName;
            if (typeName != OpenXRLoaderType)
                XRPackageMetadataStore.RemoveLoader(manager, typeName, BuildTargetGroup.Android);
        }
        if (!XRPackageMetadataStore.IsLoaderAssigned(OpenXRLoaderType, BuildTargetGroup.Android))
            XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderType, BuildTargetGroup.Android);

        EditorUtility.SetDirty(perTarget);
        EditorUtility.SetDirty(androidSettings);
        EditorUtility.SetDirty(manager);
    }

    static void ConfigureOpenXRFeatures()
    {
        OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
            throw new Exception("[QuestBuild] OpenXR settings for Android not found.");

        settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
        foreach (var feature in settings.GetFeatures())
        {
            bool wanted = AndroidFeatureTypes.Contains(feature.GetType().Name);
            if (feature.enabled != wanted)
            {
                feature.enabled = wanted;
                EditorUtility.SetDirty(feature);
            }
        }

        foreach (string typeName in AndroidFeatureTypes)
        {
            if (!settings.GetFeatures().Any(f => f.GetType().Name == typeName))
                throw new Exception($"[QuestBuild] OpenXR feature {typeName} not found for Android.");
        }
        EditorUtility.SetDirty(settings);
    }
}
