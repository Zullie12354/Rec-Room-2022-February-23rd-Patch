using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppPhoton.Realtime;
using Il2CppRecRoom.AntiCheat;
using Il2CppOrg.BouncyCastle.Crypto.Tls;
using MelonLoader;
using static Il2CppInterop.Runtime.IL2CPP;

[assembly: MelonInfo(typeof(_2022RecRoomPatch.Patch), "RecRoom 2022 Patch", "1.7.0", "Zullie")]
[assembly: MelonGame("Against Gravity", "Rec Room")]

namespace _2022RecRoomPatch
{
    // Keep this type's name/namespace intact so [MelonInfo(typeof(Patch))] still resolves after
    // obfuscation (the attribute blob stores the type name as a string). Real hook logic is in
    // the Patches class, which stays fully obfuscated.
    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]
    public class Patch : MelonMod
    {
        private static readonly HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("com.rr2022.patch");

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Patching Rec Room 2022...");

            // 1. Bypass EAC
            try
            {
                var genMethod = typeof(EACManager).GetMethod("GenerateChallengeResponse",
                    BindingFlags.Public | BindingFlags.Static);
                if (genMethod != null)
                {
                    harmony.Patch(genMethod,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.EAC_Prefix))));
                    MelonLogger.Msg("  [OK] EAC bypassed");
                }
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] EAC: {ex.Message}"); }

            // 2. Bypass TLS
            try
            {
                var notifyMethod = typeof(LegacyTlsAuthentication).GetMethod("NotifyServerCertificate");
                if (notifyMethod != null)
                {
                    harmony.Patch(notifyMethod,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.TLS_Prefix))));
                    MelonLogger.Msg("  [OK] TLS bypassed");
                }
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] TLS: {ex.Message}"); }

            // 3. Skip CheatManager
            try
            {
                var awakeMethod = typeof(Il2Cpp.CheatManager).GetMethod("Awake",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (awakeMethod != null)
                {
                    harmony.Patch(awakeMethod,
                        prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.Cheat_Prefix))));
                    MelonLogger.Msg("  [OK] CheatManager skipped");
                }
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] CheatManager: {ex.Message}"); }

            // 4. Patch Photon IDs
            try
            {
                Patches.PatchPhotonSettings();
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] Photon IDs: {ex.Message}"); }

            // 5. Point the room client at our PUN app. The build connects rooms with Rec Room's
            // own app id (a hardcoded literal), so it is swapped where Photon auth is sent:
            // IBDBKBNAABC is LoadBalancingPeer, MPPFFPILALF / HJDHOHBFOHD are OpAuthenticate /
            // OpAuthenticateOnce.
            try
            {
                var swap = new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.OpAuthenticate_Prefix)));
                int hooked = 0;
                // The client keeps the app id from the AppSettings it connects with and sends it
                // again to the master and game servers, so it is swapped there first
                // (NGGEMHLPLDH is LoadBalancingClient, MNAEFLGHAEO its ConnectUsingSettings).
                var settingsSwap = new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.ConnectUsingSettings_Prefix)));
                var classicAuth = new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.ClassicAuth_Prefix)));
                foreach (var m in typeof(Il2Cpp.NGGEMHLPLDH).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name == "MNAEFLGHAEO") { harmony.Patch(m, prefix: settingsSwap); hooked++; }
                    // ConnectUsingSettings, ConnectToMasterServer, ConnectToNameServer and the
                    // private Connect(server, proxy, type) they share.
                    if (m.Name is "MNAEFLGHAEO" or "EHDCEBCBMOH" or "ALMLAOALECL" or "JIDBIGKDIIK")
                        harmony.Patch(m, prefix: classicAuth);
                }
                foreach (var m in typeof(Il2Cpp.IBDBKBNAABC).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "MPPFFPILALF") { harmony.Patch(m, prefix: swap); hooked++; }
                    else if (m.Name == "HJDHOHBFOHD")
                    {
                        // A method takes one prefix of each kind per Patch call, so the
                        // encryption-mode prefix is applied in its own call.
                        harmony.Patch(m, prefix: swap);
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.OpAuthenticateOnce_Prefix))));
                        hooked++;
                    }
                MelonLogger.Msg($"  [OK] Photon room app swap ({hooked} auth methods)");
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] Photon room app swap: {ex.Message}"); }

            // 6. Image signatures: installed once the first scene has loaded (OnSceneWasLoaded),
            // not here. Patching touches the image manager's class, which initialises it, and
            // doing that this early is the suspected cause of Photon stalling at the name server.

            // 7. Diagnostic: log when each Photon peer connects and sets up encryption. The game
            // server's replies fail to decrypt ("Padding is invalid"), so record which server
            // each key belongs to and whether a fresh key exchange replaces the auth secret.
            try
            {
                var peer = typeof(Il2CppExitGames.Client.Photon.PhotonPeer);
                var log = new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.PeerCrypto_Prefix)));
                var secretLog = new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.InitPayloadEncryption_Prefix)));
                int hooked = 0;
                foreach (var m in peer.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    // Connect(server, appId, token, custom) / Connect(server, proxy, appId, token,
                    // custom): the app id in each server's init request is swapped too, so the
                    // master and game servers see the same app the token was issued for.
                    if (m.Name == "Connect")
                    {
                        string prefix = m.GetParameters().Length == 4 ? nameof(Patches.PeerConnect4_Prefix) : nameof(Patches.PeerConnect5_Prefix);
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Patches).GetMethod(prefix))); hooked++;
                    }
                    else if (m.Name == "EstablishEncryption" || m.Name == "Disconnect") { harmony.Patch(m, prefix: log); hooked++; }
                    else if (m.Name == "InitPayloadEncryption") { harmony.Patch(m, prefix: secretLog); hooked++; }
                }
                MelonLogger.Msg($"  [OK] Photon encryption logging ({hooked} methods)");
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] Photon encryption logging: {ex.Message}"); }

            MelonLogger.Msg("RecRoom 2022 Patch loaded!");
        }

        private static bool imageHookInstalled;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (imageHookInstalled) return;
            imageHookInstalled = true;

            // Accept images not signed by Rec Room. BIADGLBLAID is the image manager; its
            // DKEGIMLMFOE(byte[] data, byte[] signature) checks the content-signature header's
            // RSA signature against Rec Room's key (BBHEFANEPIO), which our server cannot sign
            // with ("Image signature verification failed"). The header itself is still required
            // and parsed; the server sends a well-formed one.
            try
            {
                int hooked = 0;
                foreach (var m in typeof(Il2Cpp.BIADGLBLAID).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    if (m.Name == "DKEGIMLMFOE" && m.ReturnType == typeof(bool))
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.ImageSignature_Prefix))));
                        hooked++;
                    }
                MelonLogger.Msg(hooked > 0 ? $"  [OK] Image signatures accepted (after scene {sceneName})" : "  [FAIL] Image signature check not found");
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] Image signatures: {ex.Message}"); }

            // Diagnostic: the room details load fails with a KeyNotFoundException, and Unity's
            // log shows only the outer stack. Log the exception's own stack trace, which names
            // the method whose dictionary lookup failed.
            try
            {
                foreach (var m in typeof(UnityEngine.Debug).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "LogException" && m.GetParameters().Length == 1)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.LogException_Prefix))));
                // BMBKGKABHGB is the { Boolean, String } DTO whose from-dictionary method throws.
                // Its required keys are string literals in its native code: keep a copy of that
                // code (before Harmony's detour overwrites its start) to read them later.
                foreach (var f in typeof(Il2Cpp.BMBKGKABHGB).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                    if (f.Name.StartsWith("NativeMethodInfoPtr_BJFPGPPGGLJ"))
                    {
                        var methodInfo = (IntPtr)f.GetValue(null)!;
                        Patches.SmallDtoCode = Marshal.ReadIntPtr(methodInfo);
                        Patches.SmallDtoBytes = new byte[0x600];
                        Marshal.Copy(Patches.SmallDtoCode, Patches.SmallDtoBytes, 0, Patches.SmallDtoBytes.Length);
                    }
                foreach (var m in typeof(Il2Cpp.BMBKGKABHGB).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == "BJFPGPPGGLJ")
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Patches).GetMethod(nameof(Patches.SmallDto_Prefix))));
                MelonLogger.Msg("  [OK] KeyNotFound stack logging");
            }
            catch (Exception ex) { MelonLogger.Error($"  [FAIL] KeyNotFound stack logging: {ex.Message}"); }
        }
    }

    public static class Patches
    {
        // The 2022 PUN app, whose custom auth is the server's /photon/auth2022 (the 2022 client
        // authenticates with {accountId, environment, accessToken}, which the 2026 app's auth
        // service does not accept). Voice uses the server's existing Voice app.
        private const string PUN_ID = "YOUR-ID-PUN";
        private const string VOICE_ID = "YOUR-ID-VOICE";

        public static bool EAC_Prefix(string __0, ref string __result)
        {
            MelonLogger.Msg("[PATCH] EAC bypassed");
            __result = __0;
            return false;
        }

        public static bool TLS_Prefix()
        {
            MelonLogger.Msg("[PATCH] TLS bypassed");
            return false;
        }

        private static readonly System.Collections.Generic.HashSet<string> keyNotFoundSeen = new();

        // For each distinct DTO whose from-dictionary method throws KeyNotFound: name it and
        // list the string literals (its JSON keys) its native code loads.
        public static void LogException_Prefix(Il2CppSystem.Exception __0)
        {
            try
            {
                if (__0 == null) return;
                for (var e = __0; e != null; e = e.InnerException)
                {
                    if (!(e.Message ?? "").Contains("given key")) continue;
                    string stack = e.StackTrace ?? "";
                    var m = System.Text.RegularExpressions.Regex.Match(stack,
                        @"at ([A-Za-z0-9_.+`\[\]]+)\.([A-Z]{11}) \(System\.Collections\.Generic\.Dictionary`2\[TKey,TValue\] [A-Z]{11}\)");
                    while (m.Success && m.Groups[1].Value.StartsWith("LOGEFCMGCAI")) m = m.NextMatch();
                    string dto = m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : "?";
                    if (!keyNotFoundSeen.Add(dto)) return;
                    MelonLogger.Msg($"[KEYNOTFOUND] in {dto}\n{stack}");
                    if (m.Success) LogLiterals(m.Groups[1].Value, m.Groups[2].Value);
                    return;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[KEYNOTFOUND] log failed: {ex.Message}"); }
        }

        private static void LogLiterals(string typeName, string method)
        {
            // GetType by name per assembly: GetTypes() throws on Il2Cppmscorlib's broken types.
            Type? t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("Il2Cpp." + typeName); } catch { }
                if (t != null) break;
            }
            if (t == null) { MelonLogger.Msg($"[KEYNOTFOUND] type {typeName} not found"); return; }

            // The throwing class, the classes that derive from it in the same assembly (the
            // full DTO) and its base classes: together, every key the reply is read with.
            var chain = new System.Collections.Generic.List<Type>();
            try { chain.AddRange(t.Assembly.GetTypes().Where(x => x.BaseType == t)); } catch (ReflectionTypeLoadException rtle) { chain.AddRange(rtle.Types.Where(x => x != null && x.BaseType == t)!); }
            for (var b = t; b != null && b.FullName != null && b.FullName.StartsWith("Il2Cpp."); b = b.BaseType) chain.Add(b);
            foreach (var type in chain)
                foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (f.Name.StartsWith("NativeMethodInfoPtr_" + method))
                    {
                        IntPtr code = Marshal.ReadIntPtr((IntPtr)f.GetValue(null)!);
                        var bytes = new byte[0x2000];
                        Marshal.Copy(code, bytes, 0, bytes.Length);
                        MelonLogger.Msg($"[KEYNOTFOUND] {type.Name}.{method} literals: [{string.Join(", ", ScanLiterals(code, bytes).Select(s => $"\"{s}\""))}]");
                    }
        }

        private static System.Collections.Generic.List<string> ScanLiterals(IntPtr code, byte[] bytes)
        {
            IntPtr stringClass = il2cpp_object_get_class(il2cpp_string_new("x"));
            var found = new System.Collections.Generic.List<string>();
            for (int i = 0; i + 7 <= bytes.Length; i++)
            {
                if ((bytes[i] != 0x48 && bytes[i] != 0x4C) || bytes[i + 1] != 0x8B || (bytes[i + 2] & 0xC7) != 0x05) continue;
                IntPtr slot = code + i + 7 + BitConverter.ToInt32(bytes, i + 3);
                try
                {
                    IntPtr obj = Marshal.ReadIntPtr(slot);
                    if (obj == IntPtr.Zero || Marshal.ReadIntPtr(obj) != stringClass) continue;
                    string s = Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(obj) ?? "";
                    if (!found.Contains(s)) found.Add(s);
                }
                catch { }
            }
            return found;
        }

        private static int smallDtoLogged;

        public static void SmallDto_Prefix(Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> __0)
        {
            try
            {
                if (__0 == null || smallDtoLogged >= 10) return;
                smallDtoLogged++;
                var keys = new System.Collections.Generic.List<string>();
                foreach (var k in __0.Keys) keys.Add(k);
                MelonLogger.Msg($"[SMALLDTO] {{Boolean, String}} DTO read from keys: [{string.Join(", ", keys)}]");
                // From the second call on, the method's string literals are initialised.
                if (smallDtoLogged >= 2 && SmallDtoBytes != null) LogSmallDtoLiterals();
            }
            catch (Exception ex) { MelonLogger.Warning($"[SMALLDTO] log failed: {ex.Message}"); }
        }

        public static IntPtr SmallDtoCode;
        public static byte[]? SmallDtoBytes;
        private static bool literalsLogged;

        // Every RIP-relative qword load (mov reg,[rip+disp32]) in the copied code whose target
        // holds a managed string: those are the method's string literals, i.e. the JSON keys.
        private static void LogSmallDtoLiterals()
        {
            if (literalsLogged) return;
            literalsLogged = true;
            var bytes = SmallDtoBytes!;
            IntPtr stringClass = il2cpp_object_get_class(il2cpp_string_new("x"));
            var found = new System.Collections.Generic.List<string>();
            for (int i = 0; i + 7 <= bytes.Length; i++)
            {
                if ((bytes[i] != 0x48 && bytes[i] != 0x4C) || bytes[i + 1] != 0x8B || (bytes[i + 2] & 0xC7) != 0x05) continue;
                int disp = BitConverter.ToInt32(bytes, i + 3);
                IntPtr slot = SmallDtoCode + i + 7 + disp;
                try
                {
                    IntPtr obj = Marshal.ReadIntPtr(slot);
                    if (obj == IntPtr.Zero || Marshal.ReadIntPtr(obj) != stringClass) continue;
                    found.Add(Il2CppInterop.Runtime.IL2CPP.Il2CppStringToManaged(obj) ?? "");
                }
                catch { }
            }
            MelonLogger.Msg($"[SMALLDTO] string literals in its from-dictionary code: [{string.Join(", ", found.Select(s => $"\"{s}\""))}]");
        }

        public static bool ImageSignature_Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool Cheat_Prefix()
        {
            MelonLogger.Msg("[PATCH] CheatManager skipped");
            return false;
        }

        // Rec Room's own 2022 PUN app id, hardcoded in the build (a string literal, not read from
        // PhotonServerSettings, so setting AppIdRealtime alone never reached it). Its custom auth
        // points at Rec Room's servers, so the name server answers 32755 without calling ours.
        private const string RR_PUN_ID = "9372aa8d-d3f4-44a0-986d-419e145a2b83";

        // OpAuthenticate / OpAuthenticateOnce (__0 = app id, __2 = AuthenticationValues): every
        // Photon connection authenticates through one of these, so the room client's app id is
        // swapped for ours here. Other app ids (the anonymous e93ae440 one, which works) pass.
        public static void OpAuthenticate_Prefix(ref string __0, Il2Cpp.KLFGJPOEDJM __2, MethodBase __originalMethod)
        {
            try
            {
                if (__0 == RR_PUN_ID)
                {
                    __0 = PUN_ID;
                    MelonLogger.Msg($"[PHOTON] {__originalMethod.Name}: room app {RR_PUN_ID} -> {PUN_ID} ({__2?.ToString() ?? "no AuthValues"})");
                }
                else
                    MelonLogger.Msg($"[PHOTON] {__originalMethod.Name}: app {__0}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[PHOTON] app id swap failed: {ex.Message}"); }
        }

        // OpAuthenticateOnce also carries the encryption mode (__4). The game
        // server's replies fail to decrypt ("Padding is invalid") with the mode the build picks,
        // so it is logged, and a datagram mode is replaced by plain payload encryption, which
        // every Photon Cloud server supports.
        public static void OpAuthenticateOnce_Prefix(string __0, ref Il2Cpp.DIKHNJGJLFC __4)
        {
            try
            {
                var before = __4;
                if (__4 != Il2Cpp.DIKHNJGJLFC.PayloadEncryption)
                    __4 = Il2Cpp.DIKHNJGJLFC.PayloadEncryption;
                MelonLogger.Msg($"[PHOTON] AuthenticateOnce app {__0}: encryption {before} -> {__4}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[PHOTON] encryption mode check failed: {ex.Message}"); }
        }

        public static void PeerCrypto_Prefix(Il2CppExitGames.Client.Photon.PhotonPeer __instance, MethodBase __originalMethod)
        {
            try
            {
                MelonLogger.Msg($"[CRYPTO] peer {__instance.Pointer.ToInt64():X} {__originalMethod.Name} " +
                                $"(server {__instance.ServerAddress}, encryption available {__instance.IsEncryptionAvailable})");
            }
            catch (Exception ex) { MelonLogger.Warning($"[CRYPTO] log failed: {ex.Message}"); }
        }

        public static void PeerConnect4_Prefix(Il2CppExitGames.Client.Photon.PhotonPeer __instance, string __0, ref string __1) =>
            PeerConnect(__instance, __0, ref __1);

        public static void PeerConnect5_Prefix(Il2CppExitGames.Client.Photon.PhotonPeer __instance, string __0, ref string __2) =>
            PeerConnect(__instance, __0, ref __2);

        private static void PeerConnect(Il2CppExitGames.Client.Photon.PhotonPeer peer, string server, ref string appId)
        {
            try
            {
                string was = appId;
                if (appId == RR_PUN_ID) appId = PUN_ID;
                MelonLogger.Msg($"[CRYPTO] peer {peer.Pointer.ToInt64():X} Connect to {server} app {(was == appId ? appId : $"{was} -> {appId}")}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[CRYPTO] connect swap failed: {ex.Message}"); }
        }

        // Logs a short hash of the secret, never the secret itself.
        public static void InitPayloadEncryption_Prefix(Il2CppExitGames.Client.Photon.PhotonPeer __instance,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> __0)
        {
            try
            {
                string hash = "null";
                if (__0 != null)
                {
                    var bytes = new byte[__0.Length];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = __0[i];
                    hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Substring(0, 12) + $" ({bytes.Length} bytes)";
                }
                MelonLogger.Msg($"[CRYPTO] peer {__instance.Pointer.ToInt64():X} InitPayloadEncryption secret {hash} " +
                                $"(server {__instance.ServerAddress})\n{Environment.StackTrace}");
            }
            catch (Exception ex) { MelonLogger.Warning($"[CRYPTO] log failed: {ex.Message}"); }
        }

        // JDAJELOOCCD is LoadBalancingClient.AuthMode. The room client uses AuthOnce: it gets one
        // encryption secret at the name server and reuses it, and our app's game servers reply
        // with a key that secret does not open ("Padding is invalid"). Classic Auth does its own
        // key exchange and authenticates with the token on every server, as the voice client
        // does successfully.
        public static void ClassicAuth_Prefix(Il2Cpp.NGGEMHLPLDH __instance, MethodBase __originalMethod)
        {
            try
            {
                if (__instance.JDAJELOOCCD != Il2Cpp.JPIODNNCOCJ.Auth)
                {
                    MelonLogger.Msg($"[PHOTON] {__originalMethod.Name}: auth mode {__instance.JDAJELOOCCD} -> Auth");
                    __instance.JDAJELOOCCD = Il2Cpp.JPIODNNCOCJ.Auth;
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[PHOTON] auth mode switch failed: {ex.Message}"); }
        }

        public static void ConnectUsingSettings_Prefix(AppSettings __0)
        {
            try
            {
                if (__0 != null && __0.AppIdRealtime == RR_PUN_ID)
                {
                    __0.AppIdRealtime = PUN_ID;
                    MelonLogger.Msg($"[PHOTON] ConnectUsingSettings: room app {RR_PUN_ID} -> {PUN_ID} (auth mode {__0.AuthMode})");
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[PHOTON] settings app id swap failed: {ex.Message}"); }
        }

        public static void PatchPhotonSettings()
        {
            MelonLogger.Msg("[PATCH] Patching Photon settings...");

            var settingsObj = UnityEngine.Resources.Load("PhotonServerSettings");
            if (settingsObj == null)
            {
                MelonLogger.Warning("  PhotonServerSettings not found in Resources!");
                return;
            }

            var objPtr = settingsObj.Pointer;
            if (objPtr == IntPtr.Zero) { MelonLogger.Warning("  Pointer is null"); return; }

            var objClass = il2cpp_object_get_class(objPtr);

            // Get the AppSettings field at offset 0x18 from dump.cs
            var appSettingsPtr = Marshal.ReadIntPtr(objPtr, 0x18);
            if (appSettingsPtr == IntPtr.Zero)
            {
                MelonLogger.Warning("  AppSettings is null at offset 0x18");
                return;
            }
            MelonLogger.Msg($"  AppSettings ptr: 0x{appSettingsPtr:X}");

            var asClass = il2cpp_object_get_class(appSettingsPtr);

            // Set fields using il2cpp_field_set_value
            SetField(asClass, appSettingsPtr, "AppIdRealtime", PUN_ID);
            SetField(asClass, appSettingsPtr, "AppIdVoice", VOICE_ID);
            SetField(asClass, appSettingsPtr, "AppVersion", "20220223");
            SetField(asClass, appSettingsPtr, "FixedRegion", "us");

            MelonLogger.Msg("  [OK] Photon IDs set!");
        }

        private static void SetField(IntPtr objClass, IntPtr objPtr, string fieldName, string value)
        {
            var field = il2cpp_class_get_field_from_name(objClass, fieldName);
            if (field == IntPtr.Zero)
            {
                MelonLogger.Warning($"  Field '{fieldName}' not found");
                return;
            }
            var strPtr = il2cpp_string_new(value);
            // il2cpp_field_set_value takes void* for the value, so use unsafe or IntPtr
            unsafe
            {
                il2cpp_field_set_value(objPtr, field, (void*)strPtr);
            }
            MelonLogger.Msg($"  Set {fieldName} = {value}");
        }
    }
}
