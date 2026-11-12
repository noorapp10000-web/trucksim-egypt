#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TruckSim.EditorTools
{
    /// <summary>One-click project setup and builds (also used from the command line in batch mode).</summary>
    public static class ProjectBuilder
    {
        const string ScenePath = "Assets/Scenes/Highway.unity";

        [MenuItem("Truck Sim/1. Setup Project (scene + settings)")]
        public static void Setup()
        {
            AssetDatabase.Refresh();
            ConfigureTextures();
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("GameManager").AddComponent<GameManager>();
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            PlayerSettings.companyName = "TruckSim";
            PlayerSettings.productName = "Highway Truck Simulator";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.trucksim.highway");
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            // Keep the glTF shaders in every build.
            AddAlwaysIncludedShaders("glTF/PbrMetallicRoughness", "glTF/PbrSpecularGlossiness", "glTF/Unlit", "Skybox/Procedural", "Standard",
                "Unlit/Color", "Legacy Shaders/Particles/Additive", "Particles/Standard Unlit");  // truck lamp lenses + glow sprites (Shader.Find at runtime)
            AssetDatabase.SaveAssets();
            Debug.Log("[ProjectBuilder] Setup complete");
        }

        static void AddAlwaysIncludedShaders(params string[] names)
        {
            var gs = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            foreach (var n in names)
            {
                var sh = Shader.Find(n);
                if (sh == null) { Debug.Log("[ProjectBuilder] shader not found (ok): " + n); continue; }
                bool exists = false;
                for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == sh) exists = true;
                if (exists) continue;
                arr.InsertArrayElementAtIndex(arr.arraySize);
                arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = sh;
            }
            so.ApplyModifiedProperties();
        }

        static void ConfigureTextures()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Resources/UI", "Assets/Resources/Textures" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) continue;
                bool ui = path.Contains("/UI/");
                ti.textureType = TextureImporterType.Default;
                ti.alphaIsTransparency = ui;
                ti.mipmapEnabled = !ui;
                ti.wrapMode = ui ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                ti.anisoLevel = ui ? 1 : 8;
                ti.textureCompression = ui ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
                ti.maxTextureSize = 1024;
                ti.SaveAndReimport();
            }
        }

        [MenuItem("Truck Sim/2. Build Linux")]
        public static void BuildLinux() => Build(BuildTarget.StandaloneLinux64, "Builds/Linux/HighwayTruckSim.x86_64");

        [MenuItem("Truck Sim/3. Build Windows")]
        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Builds/Windows/HighwayTruckSim.exe");

        [MenuItem("Truck Sim/4. Build Android APK")]
        public static void BuildAndroid()
        {
            // 64-bit ARM (required by modern phones / Google Play) via IL2CPP, plain installable APK.
            var android = UnityEditor.Build.NamedBuildTarget.Android;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.bundleVersion = "3.0";
            PlayerSettings.Android.bundleVersionCode = 3;
            EditorUserBuildSettings.buildAppBundle = false;
            EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Disabled;
            Build(BuildTarget.Android, "Builds/Android/HighwayTruckSim.apk");
        }

        static void Build(BuildTarget target, string output)
        {
            Setup();
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var opts = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = output,
                target = target,
                options = BuildOptions.None
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Debug.Log($"[ProjectBuilder] Build {target}: {report.summary.result}, size {report.summary.totalSize / (1024 * 1024)} MB, errors {report.summary.totalErrors}");
            if (Application.isBatchMode) EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }
}
#endif
