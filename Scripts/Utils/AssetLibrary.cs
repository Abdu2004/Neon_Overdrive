using Godot;
using System.Collections.Generic;

namespace ApexCombatRacing.Utils;

/// <summary>
/// Central loader for the Kenney art packs. Every <c>.glb</c> in the project is
/// loaded through here so there is exactly one place to change how models are
/// resolved.
///
/// Two paths are supported on purpose:
///   * <b>Imported</b> — when a <c>&lt;path&gt;.import</c> sidecar exists (i.e. the
///     editor has imported the asset) we use <see cref="ResourceLoader"/>, which
///     gives the fast, cached, binary-imported version.
///   * <b>Runtime fallback</b> — otherwise we parse the GLB with
///     <see cref="GltfDocument"/>. This keeps a fresh clone runnable before the
///     editor has ever touched the assets, which is the case in this repository
///     (no <c>.import</c> files are committed).
///
/// Loaded templates are cached and duplicated on demand so each car gets its own
/// instance (and its own tinted materials) without reparsing the file.
/// </summary>
public static class AssetLibrary
{
    // Kenney Car Kit model folder (the .glb files live in "GLB format/").
    public const string CarModelDir = "res://Assets/Models/Cars/Models/GLB format/";

    private static readonly Dictionary<string, Node3D> _templates = new();

    /// <summary>Build a <c>res://</c> path for a car-kit model by file name (no extension).</summary>
    public static string CarModel(string name) => CarModelDir + name + ".glb";

    /// <summary>
    /// Load a model and return a fresh instance, or <c>null</c> if it could not be
    /// parsed. Never throws — a missing asset only becomes a warning.
    /// </summary>
    public static Node3D? LoadModel(string resPath)
    {
        if (string.IsNullOrEmpty(resPath))
        {
            return null;
        }

        if (!_templates.TryGetValue(resPath, out Node3D? template))
        {
            template = CreateTemplate(resPath);
            if (template != null)
            {
                _templates[resPath] = template;
            }
        }

        if (template == null)
        {
            return null;
        }

        // Duplicate so callers can tint / animate without touching the cache.
        return template.Duplicate() as Node3D;
    }

    private static Node3D? CreateTemplate(string resPath)
    {
        // Fast path: asset already imported by the editor.
        if (FileAccess.FileExists(resPath + ".import"))
        {
            var scene = ResourceLoader.Load<PackedScene>(resPath);
            if (scene != null)
            {
                return scene.Instantiate<Node3D>();
            }
        }

        // Slow path: parse the GLB at runtime.
        var document = new GltfDocument();
        var state = new GltfState();
        Error error = document.AppendFromFile(ProjectSettings.GlobalizePath(resPath), state);
        if (error != Error.Ok)
        {
            GD.PushWarning($"AssetLibrary: failed to load '{resPath}' ({error}).");
            return null;
        }

        return document.GenerateScene(state) as Node3D;
    }

    /// <summary>
    /// Tint the model's body mesh. Only the mesh node named "body" is touched,
    /// and only its surface materials are duplicated, so the shared Kenney palette
    /// stays intact for other instances. <paramref name="strength"/> is a 0..1
    /// lerp from the original colour toward <paramref name="color"/>.
    /// </summary>
    public static void TintBody(Node3D model, Color color, float strength)
    {
        strength = Mathf.Clamp(strength, 0.0f, 1.0f);
        if (strength <= 0.0f)
        {
            return;
        }

        Color tint = Colors.White.Lerp(color, strength);

        foreach (Node child in model.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D meshInstance)
            {
                continue;
            }

            if (!meshInstance.Name.ToString().Equals("body", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Mesh? mesh = meshInstance.Mesh;
            if (mesh == null)
            {
                continue;
            }

            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                if (meshInstance.GetActiveMaterial(surface) is not StandardMaterial3D material)
                {
                    continue;
                }

                var copy = (StandardMaterial3D)material.Duplicate();
                copy.AlbedoColor = copy.AlbedoColor * tint;
                meshInstance.SetSurfaceOverrideMaterial(surface, copy);
            }
        }
    }

    /// <summary>Disable shadow casting on every mesh (cheap arena dressing).</summary>
    public static void SetShadows(Node3D root, bool cast)
    {
        foreach (Node child in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is MeshInstance3D meshInstance)
            {
                meshInstance.CastShadow = cast
                    ? GeometryInstance3D.ShadowCastingSetting.On
                    : GeometryInstance3D.ShadowCastingSetting.Off;
            }
        }
    }

    /// <summary>Find a named part anywhere under <paramref name="root"/>.</summary>
    public static Node3D? FindPart(Node root, string name)
        => root.FindChild(name, true, false) as Node3D;
}
