using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using Mono.Cecil;

namespace GauntletMod.Patcher
{
    /// <summary>
    /// BepInEx 5 preloader patcher. This runs before Unity ever loads
    /// Assembly-CSharp, on the in-memory Cecil AssemblyDefinition - nothing on disk
    /// is modified.
    ///
    /// Its only job is a *targeted* publicise of the handful of Silksong types the
    /// Custom Gauntlet plugin has to reach into (menu internals, respawn plumbing).
    /// The plugin itself is still built against the stock assemblies and uses
    /// reflection, so it works with or without this patcher - but with it, those
    /// members are genuinely public, which is what later phases (wave spawning,
    /// suppressing the room's own FSMs) will need in order to touch them directly.
    ///
    /// Two rules keep this safe on a Unity assembly:
    ///
    ///   1. Only the listed types are touched. A blanket publicise of a 7 MB game
    ///      assembly is slow and is a good way to break something unrelated.
    ///
    ///   2. Fields that Unity was *not* already serialising get the NotSerialized
    ///      flag when they're made public. Unity serialises public fields by
    ///      default, so without this, publicising a plain private field would quietly
    ///      add it to the serialised set and change how scenes and prefabs
    ///      deserialise. Fields that already carry [SerializeField] were serialised
    ///      before and after, so they're left alone.
    /// </summary>
    public static class Patcher
    {
        private static readonly ManualLogSource Log =
            Logger.CreateLogSource("GauntletMod.Patcher");

        /// <summary>Types we publicise, by full name (nested types come along with their parent).</summary>
        private static readonly HashSet<string> Targets = new HashSet<string>
        {
            "UIManager",
            "GameManager",
            "HeroController",
            "PlayerData",
            "MenuScreen",
            "MenuButtonList",
            "MenuButtonListCondition",
            "StartGameEventTrigger",
            "RespawnMarker",
            "RespawnTrigger",
            "TransitionPoint",
            "RestBench",
            "CustomSceneManager",
            "HealthManager",
            "DamageHero",
        };

        /// <summary>Required by BepInEx: which assemblies we want handed to Patch().</summary>
        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        public static void Patch(AssemblyDefinition assembly)
        {
            int types = 0, fields = 0, methods = 0;

            foreach (TypeDefinition type in assembly.MainModule.Types)
            {
                if (!Targets.Contains(type.FullName)) continue;

                Publicise(type, ref types, ref fields, ref methods);
            }

            Log.LogInfo($"Publicised {types} type(s), {fields} field(s), {methods} method(s) in Assembly-CSharp.");

            string[] missing = Targets
                .Where(t => assembly.MainModule.Types.All(td => td.FullName != t))
                .ToArray();

            if (missing.Length > 0)
            {
                // Not fatal: the plugin falls back to reflection. But it means a game
                // patch renamed something, which is worth knowing about early.
                Log.LogWarning("Types not found (game update?): " + string.Join(", ", missing));
            }
        }

        private static void Publicise(TypeDefinition type, ref int types, ref int fields, ref int methods)
        {
            if (type.IsNested)
            {
                if (!type.IsNestedPublic) { type.IsNestedPublic = true; types++; }
            }
            else if (!type.IsPublic)
            {
                type.IsPublic = true;
                types++;
            }

            foreach (FieldDefinition field in type.Fields)
            {
                if (field.IsPublic) continue;

                // See rule 2 in the class comment.
                bool wasSerialised = field.CustomAttributes
                    .Any(a => a.AttributeType.Name == "SerializeField");

                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
                if (!wasSerialised) field.IsNotSerialized = true;

                fields++;
            }

            foreach (MethodDefinition method in type.Methods)
            {
                if (method.IsPublic) continue;

                // Explicit interface implementations must stay private or the CLR
                // stops matching them to the interface slot.
                if (method.HasOverrides) continue;

                method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
                methods++;
            }

            foreach (TypeDefinition nested in type.NestedTypes)
            {
                Publicise(nested, ref types, ref fields, ref methods);
            }
        }
    }
}
