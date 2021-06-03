using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using UniGLTF;
using UnityEngine;
using ValheimPlus.VRM;
using VRM;
using IniParser;
using IniParser.Model;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;

namespace ValheimPlus
{
	public static class AccessUtil
	{
		public static Tout GetField<Tin, Tout>(this Tin self, string fieldName)
		{
			return AccessTools.FieldRefAccess<Tin, Tout>(fieldName).Invoke(self);
		}
	}

	public class AvatarSetting
    {
		public string model { get; set; }
    }

	[HarmonyPatch(typeof(Shader))]
	[HarmonyPatch(nameof(Shader.Find))]
	static class ShaderPatch
	{
		static bool Prefix(ref Shader __result, string name)
		{
			if (VPlusShaders.Shaders.TryGetValue(name, out var shader))
			{
				__result = shader;
				return false;
			}

			return true;
		}
	}

	public static class VPlusShaders
	{
		public static Dictionary<string, Shader> Shaders { get; } = new Dictionary<string, Shader>();
		public static Dictionary<string, GameObject> Models { get; } = new Dictionary<string, GameObject>();
		public static Dictionary<Player, GameObject> Players { get; } = new Dictionary<Player, GameObject>();
		public static Dictionary<Player, string> PlayerNames { get; } = new Dictionary<Player, string>();
		public static string VPlusAvatars = Paths.BepInExRootPath + Path.DirectorySeparatorChar + "vplus-avatar";

		public static void Load()
		{
			var bundlePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), @"VPlus.shaders");
			if (File.Exists(bundlePath))
			{
				var assetBundle = AssetBundle.LoadFromFile(bundlePath);
				var assets = assetBundle.LoadAllAssets<Shader>();
				foreach (var asset in assets)
				{
					UnityEngine.Debug.Log("Add Shader: " + asset.name);
					Shaders.Add(asset.name, asset);
				}
			}

			LoadModel("default");
		}

		public static string GetModel(string playerName)
        {
			playerName = playerName.ToLower();
			var path = VPlusAvatars + Path.DirectorySeparatorChar + "avatar.ini";

			FileIniDataParser parser = new FileIniDataParser();
			IniData config = parser.ReadFile(path);

			string model = config["Avatars"][playerName] ?? "default";
			UnityEngine.Debug.LogError($"{playerName} load with model {model}");

			foreach(KeyData keyData in config["Avatars"]) {
				LoadModel(keyData.Value);
            }

			return model;
        }

		public static void LoadModel(String name)
		{
			var scale = 1.1f;
			var brightness = 0.8f;

			if (!VPlusShaders.Models.ContainsKey(name)) {
				var path = VPlusAvatars + Path.DirectorySeparatorChar + $"{name}.vrm";
				if (File.Exists(path))
				{
					var orgVrm = ImportVRM(path, scale);

					if (orgVrm != null)
					{
						GameObject.DontDestroyOnLoad(orgVrm);
						Models[name] = orgVrm;

						var materials = new List<Material>();

						foreach (var smr in orgVrm.GetComponentsInChildren<SkinnedMeshRenderer>())
						{
							foreach (var mat in smr.materials)
							{
								if (!materials.Contains(mat)) materials.Add(mat);
							}
						}

						foreach (var mr in orgVrm.GetComponentsInChildren<MeshRenderer>())
						{
							foreach (var mat in mr.materials)
							{
								if (!materials.Contains(mat)) materials.Add(mat);
							}
						}

						var shader = Shader.Find("Custom/Player");
						foreach (var mat in materials)
						{
							if (mat.shader == shader) continue;

							var color = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;

							var mainTex = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") as Texture2D : null;
							Texture2D tex = mainTex;
							if (mainTex != null)
							{
								tex = new Texture2D(mainTex.width, mainTex.height);
								var colors = mainTex.GetPixels();
								for (var i = 0; i < colors.Length; i++)
								{
									var col = colors[i] * color;
									float h, s, v;
									Color.RGBToHSV(col, out h, out s, out v);
									v *= brightness;
									colors[i] = Color.HSVToRGB(h, s, v);
									colors[i].a = col.a;
								}
								tex.SetPixels(colors);
								tex.Apply();
							}

							var bumpMap = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
							mat.shader = shader;

							mat.SetTexture("_MainTex", tex);
							mat.SetTexture("_SkinBumpMap", bumpMap);
							mat.SetColor("_SkinColor", color);
							mat.SetTexture("_ChestTex", tex);
							mat.SetTexture("_ChestBumpMap", bumpMap);
							mat.SetTexture("_LegsTex", tex);
							mat.SetTexture("_LegsBumpMap", bumpMap);
							mat.SetFloat("_Glossiness", 0.2f);
							mat.SetFloat("_MetalGlossiness", 0.0f);
						}

						var lodGroup = orgVrm.AddComponent<LODGroup>();
						var lod = new LOD(0.1f, orgVrm.GetComponentsInChildren<SkinnedMeshRenderer>());
						lodGroup.SetLODs(new LOD[] { lod });
						lodGroup.RecalculateBounds();

						var orgLodGroup = orgVrm.GetComponentInChildren<LODGroup>();
						lodGroup.fadeMode = orgLodGroup.fadeMode;
						lodGroup.animateCrossFading = orgLodGroup.animateCrossFading;

						orgVrm.SetActive(false);
					}
				} 
				else
                {
					UnityEngine.Debug.LogError($"Model not found: {path}");
                }
			}
		}

		private static GameObject ImportVRM(string path, float scale)
		{
			try
			{
				// 1. GltfParser を呼び出します。
				//    GltfParser はファイルから JSON 情報とバイナリデータを読み出します。
				var parser = new GltfParser();
				parser.ParsePath(path);

				// 2. GltfParser のインスタンスを引数にして VRMImporterContext を作成します。
				//    VRMImporterContext は VRM のロードを実際に行うクラスです。
				using (var context = new VRMImporterContext(parser))
				{
					// 3. Load 関数を呼び出し、VRM の GameObject を生成します。
					context.Load();

					// 4. （任意） SkinnedMeshRenderer の UpdateWhenOffscreen を有効にできる便利関数です。
					context.EnableUpdateWhenOffscreen();

					// 5. VRM モデルを表示します。
					context.ShowMeshes();

					// 6. VRM の GameObject が実際に使用している UnityEngine.Object リソースの寿命を VRM の GameObject に紐付けます。
					//    つまり VRM の GameObject の破棄時に、実際に使用しているリソース (Texture, Material, Mesh, etc) をまとめて破棄することができます。
					context.DisposeOnGameObjectDestroyed();

					context.Root.transform.localScale *= scale;

					Debug.Log("[ValheimVRM] VRM読み込み成功");
					Debug.Log("[ValheimVRM] VRMファイルパス: " + path);

					// 7. Root の GameObject を return します。
					//    Root の GameObject とは VRMMeta コンポーネントが付与されている GameObject のことです。
					return context.Root;
				}
			}
			catch (Exception ex)
			{
				Debug.LogError(ex);
			}

			return null;
		}
	}

	[HarmonyPatch(typeof(VisEquipment), "UpdateLodgroup")]
	static class Patch_VisEquipment_UpdateLodgroup
	{
		[HarmonyPostfix]
		static void Postfix(VisEquipment __instance)
		{
			if (!__instance.m_isPlayer) return;
			var player = __instance.GetComponent<Player>();
			if (player == null || !VPlusShaders.Players.ContainsKey(player)) return;

			var hair = __instance.GetField<VisEquipment, GameObject>("m_hairItemInstance");
			if (hair != null) SetVisible(hair, false);

			var beard = __instance.GetField<VisEquipment, GameObject>("m_beardItemInstance");
			if (beard != null) SetVisible(beard, false);

			var chestList = __instance.GetField<VisEquipment, List<GameObject>>("m_chestItemInstances");
			if (chestList != null) foreach (var chest in chestList) SetVisible(chest, false);

			var legList = __instance.GetField<VisEquipment, List<GameObject>>("m_legItemInstances");
			if (legList != null) foreach (var leg in legList) SetVisible(leg, false);

			var shoulderList = __instance.GetField<VisEquipment, List<GameObject>>("m_shoulderItemInstances");
			if (shoulderList != null) foreach (var shoulder in shoulderList) SetVisible(shoulder, false);

			var utilityList = __instance.GetField<VisEquipment, List<GameObject>>("m_utilityItemInstances");
			if (utilityList != null) foreach (var utility in utilityList) SetVisible(utility, false);

			var helmet = __instance.GetField<VisEquipment, GameObject>("m_helmetItemInstance");
			if (helmet != null) SetVisible(helmet, false);

			var name = VPlusShaders.PlayerNames[player];

			var leftItem = __instance.GetField<VisEquipment, GameObject>("m_leftItemInstance");
			if (leftItem != null) leftItem.transform.localPosition = Vector3.zero;

			var rightItem = __instance.GetField<VisEquipment, GameObject>("m_rightItemInstance");
			if (rightItem != null) rightItem.transform.localPosition = Vector3.zero;
		}

		private static void SetVisible(GameObject obj, bool flag)
		{
			foreach (var mr in obj.GetComponentsInChildren<MeshRenderer>()) mr.enabled = flag;
			foreach (var smr in obj.GetComponentsInChildren<SkinnedMeshRenderer>()) smr.enabled = flag;
		}
	}

	[HarmonyPatch(typeof(Humanoid), "OnRagdollCreated")]
	static class Patch_Humanoid_OnRagdollCreated
	{
		[HarmonyPostfix]
		static void Postfix(Humanoid __instance, Ragdoll ragdoll)
		{
			if (!__instance.IsPlayer()) return;

			foreach (var smr in ragdoll.GetComponentsInChildren<SkinnedMeshRenderer>())
			{
				smr.forceRenderingOff = true;
				smr.updateWhenOffscreen = true;
			}

			var ragAnim = ragdoll.gameObject.AddComponent<Animator>();
			ragAnim.keepAnimatorControllerStateOnDisable = true;
			ragAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

			var orgAnim = ((Player)__instance).GetField<Player, Animator>("m_animator");
			ragAnim.avatar = orgAnim.avatar;

			if (VPlusShaders.Players.TryGetValue((Player)__instance, out var vrm))
			{
				vrm.transform.SetParent(ragdoll.transform);
				vrm.GetComponent<VRMAnimationSync>().Setup(ragAnim, true);
			}
		}
	}

	[HarmonyPatch(typeof(Character), "SetVisible")]
	static class Patch_Character_SetVisible
	{
		[HarmonyPostfix]
		static void Postfix(Character __instance, bool visible)
		{
			if (!__instance.IsPlayer()) return;

			if (VPlusShaders.Players.TryGetValue((Player)__instance, out var vrm))
			{
				var lodGroup = vrm.GetComponent<LODGroup>();
				if (visible)
				{
					lodGroup.localReferencePoint = __instance.GetField<Character, Vector3>("m_originalLocalRef");
				}
				else
				{
					lodGroup.localReferencePoint = new Vector3(999999f, 999999f, 999999f);
				}
			}
		}
	}

	[HarmonyPatch(typeof(Player), "OnDeath")]
	static class Patch_Player_OnDeath
	{
		[HarmonyPostfix]
		static void Postfix(Player __instance)
		{
			string name = null;
			if (VPlusShaders.PlayerNames.ContainsKey(__instance)) name = VPlusShaders.PlayerNames[__instance];
			if (name != null) {
				GameObject.Destroy(__instance.GetComponent<VRMEyePositionSync>());
			}
		}
	}


	[HarmonyPatch(typeof(Player), "Awake")]
	static class Patch_Player_Awake
	{
		// private static Dictionary<string, GameObject> vrmDic = new Dictionary<string, GameObject>();
		// private static Dictionary<string, byte[]> vrmBufDic = new Dictionary<string, byte[]>();

		[HarmonyPostfix] 
		static void Postfix(Player __instance)
		{
			// Get Player Name
			string playerName = GetPlayerName(__instance);
			string modelName = VPlusShaders.GetModel(playerName);

			var offsetY = 0.0f;
			var fixCameraHeight = false;

			// Try Load Model
			if (!VPlusShaders.Models.ContainsKey(modelName)) {
				VPlusShaders.LoadModel(modelName);
			}

			// Load Model
			if (VPlusShaders.Models.ContainsKey(modelName))
			{
				var vrmModel = GameObject.Instantiate(VPlusShaders.Models[modelName]);
				VPlusShaders.Players[__instance] = vrmModel;
				VPlusShaders.PlayerNames[__instance] = playerName;

				vrmModel.SetActive(true);
				vrmModel.transform.SetParent(__instance.GetComponentInChildren<Animator>().transform.parent, false);

				foreach (var smr in __instance.GetVisual().GetComponentsInChildren<SkinnedMeshRenderer>())
				{
					smr.forceRenderingOff = true;
					smr.updateWhenOffscreen = true;
				}

				var orgAnim = AccessTools.FieldRefAccess<Player, Animator>(__instance, "m_animator");
				orgAnim.keepAnimatorControllerStateOnDisable = true;
				orgAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

				vrmModel.transform.localPosition = orgAnim.transform.localPosition;

				if (vrmModel.GetComponent<VRMAnimationSync>() == null) {
					vrmModel.AddComponent<VRMAnimationSync>().Setup(orgAnim, false, offsetY);
				} else {
					vrmModel.GetComponent<VRMAnimationSync>().Setup(orgAnim, false, offsetY);
				}

				if (fixCameraHeight)
				{
					var vrmEye = vrmModel.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.LeftEye);
					if (vrmEye == null) vrmEye = vrmModel.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Head);
					if (vrmEye == null) vrmEye = vrmModel.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Neck);
					if (vrmEye != null)
					{
						if (__instance.gameObject.GetComponent<VRMEyePositionSync>() == null) __instance.gameObject.AddComponent<VRMEyePositionSync>().Setup(vrmEye);
						else __instance.gameObject.GetComponent<VRMEyePositionSync>().Setup(vrmEye);
					}
				}
			}
		}

		private static string GetPlayerName(Player __instance)
        {
			string playerName = null;
			if (Game.instance != null)
			{
				playerName = __instance.GetPlayerName();
				if (playerName == "" || playerName == "...") playerName = Game.instance.GetPlayerProfile().GetName();
			}
			else
			{
				var index = FejdStartup.instance.GetField<FejdStartup, int>("m_profileIndex");
				var profiles = FejdStartup.instance.GetField<FejdStartup, List<PlayerProfile>>("m_profiles");
				if (index >= 0 && index < profiles.Count) playerName = profiles[index].GetName();
			}

			return playerName;
		}

	}
}
