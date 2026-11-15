using Godot;
using ApexCombatRacing.Combat;
using ApexCombatRacing.Vehicles;

namespace ApexCombatRacing.Utils;

/// <summary>
/// Phase 1 test arena, built entirely from code.
///
/// The project ships no imported meshes and no authored node trees: this node is
/// the only thing in <c>Main.tscn</c>, and it creates the sky, sun, ground, landmark
/// props, the player car and the chase camera at runtime. That guarantees there is
/// never a broken <c>res://</c> reference or a missing asset error, and it keeps the
/// scene light enough for integrated GPUs.
///
/// Everything here is intentionally cheap:
/// * one box for the ground (single draw call, no collision mesh subdivision),
/// * one directional light casting into a 1024px atlas (set in project.godot),
/// * a ring of unlit emissive pillars purely so speed is readable,
/// * shadows disabled on all props except the car.
/// </summary>
[GlobalClass]
public partial class SceneBuilder : Node3D
{
    [ExportGroup("Arena")]
    [Export(PropertyHint.Range, "50,2000,10")] public float GroundSizeMeters { get; set; } = 400.0f;
    [Export(PropertyHint.Range, "0,64,1")] public int LandmarkCount { get; set; } = 16;
    [Export(PropertyHint.Range, "10,200,1")] public float LandmarkRadius { get; set; } = 55.0f;

    [ExportGroup("Player")]
    [Export] public Color PlayerColor { get; set; } = new Color(0.10f, 0.90f, 1.00f);

    [ExportGroup("Combat")]
    [Export(PropertyHint.Range, "0,16,1")] public int PowerUpPadCount { get; set; } = 6;
    [Export(PropertyHint.Range, "8,120,1")] public float PowerUpPadRadius { get; set; } = 22.0f;
    [Export(PropertyHint.Range, "0,8,1")] public int RivalCount { get; set; } = 3;

    public CarController? Player { get; private set; }
    public SmoothFollowCamera? ChaseCamera { get; private set; }

    public override void _Ready()
    {
        base._Ready();

        // Guard against double-building (e.g. if the scene is ever authored in-editor later).
        if (GetChildCount() > 0)
        {
            return;
        }

        BuildEnvironment();
        BuildSun();
        BuildGround();
        BuildLandmarks();
        BuildArenaProps();
        SpawnPlayer();
        SpawnRivals();
        BuildPowerUpPads();
        BuildCamera();
    }

    // =====================================================================
    // Sky + environment lighting
    // =====================================================================

    private void BuildEnvironment()
    {
        // ProceduralSkyMaterial: no texture, no HDR probe, computed on the GPU from
        // a handful of gradients — the cheapest usable sky in the Compatibility renderer.
        var skyMaterial = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.04f, 0.05f, 0.14f),      // deep night blue
            SkyHorizonColor = new Color(0.55f, 0.20f, 0.55f),  // neon haze
            SkyCurve = 0.12f,
            SkyEnergyMultiplier = 1.0f,
            GroundBottomColor = new Color(0.02f, 0.02f, 0.04f),
            GroundHorizonColor = new Color(0.12f, 0.06f, 0.16f),
            GroundCurve = 0.08f,
            SunAngleMax = 10.0f,
            SunCurve = 0.2f,
        };

        var sky = new Sky { SkyMaterial = skyMaterial };

        var environment = new Environment
        {
            BackgroundMode = Environment.BGMode.Sky,
            Sky = sky,

            // Ambient comes free from the sky radiance; no extra lights required.
            AmbientLightSource = Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 0.7f,
            AmbientLightEnergy = 0.65f,
            ReflectedLightSource = Environment.ReflectionSource.Sky,

            // Filmic tonemapping keeps neon highlights from clipping to white.
            TonemapMode = Environment.ToneMapper.Filmic,
            TonemapExposure = 1.0f,

            // Explicitly off: these are the expensive effects we are trading away
            // for low-end performance.
            GlowEnabled = false,
            SsaoEnabled = false,
            SsilEnabled = false,
            SdfgiEnabled = false,
        };

        AddChild(new WorldEnvironment { Name = "WorldEnvironment", Environment = environment });
    }

    // =====================================================================
    // Sun
    // =====================================================================

    private void BuildSun()
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-48.0f, -35.0f, 0.0f),
            LightColor = new Color(1.0f, 0.85f, 0.80f),
            LightEnergy = 1.15f,
            ShadowEnabled = true,
            // Single orthogonal cascade = one shadow pass, which is all we can afford.
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Orthogonal,
            DirectionalShadowMaxDistance = 110.0f,
            ShadowBias = 0.04f,
            ShadowNormalBias = 1.5f,
        };

        AddChild(sun);
    }

    // =====================================================================
    // Ground
    // =====================================================================

    private void BuildGround()
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.055f, 0.055f, 0.075f),
            Roughness = 0.95f,
            Metallic = 0.1f,
        };

        var halfHeight = 0.5f;

        var body = new StaticBody3D
        {
            Name = "Ground",
            // Static bodies never move, so the physics server can treat this as
            // a broad-phase-friendly immovable collider.
            Position = new Vector3(0.0f, -halfHeight, 0.0f),
        };

        body.AddChild(new CollisionShape3D
        {
            Name = "GroundShape",
            Shape = new BoxShape3D { Size = new Vector3(GroundSizeMeters, halfHeight * 2.0f, GroundSizeMeters) },
        });

        body.AddChild(new MeshInstance3D
        {
            Name = "GroundMesh",
            Mesh = new BoxMesh
            {
                Size = new Vector3(GroundSizeMeters, halfHeight * 2.0f, GroundSizeMeters),
                Material = material,
            },
            // The ground only receives shadows; skipping it as a caster halves the
            // shadow pass work for zero visual gain.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        AddChild(body);
    }

    // =====================================================================
    // Landmarks (speed reference props)
    // =====================================================================

    private void BuildLandmarks()
    {
        if (LandmarkCount <= 0)
        {
            return;
        }

        var cyan = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.55f, 0.70f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.95f, 1.00f),
            EmissionEnergyMultiplier = 2.0f,
        };

        var magenta = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.05f, 0.40f),
            EmissionEnabled = true,
            Emission = new Color(1.00f, 0.10f, 0.65f),
            EmissionEnergyMultiplier = 2.0f,
        };

        var pillarMesh = new BoxMesh { Size = new Vector3(1.2f, 6.0f, 1.2f) };

        for (int i = 0; i < LandmarkCount; i++)
        {
            float angle = Mathf.Tau * i / LandmarkCount;
            var position = new Vector3(
                Mathf.Cos(angle) * LandmarkRadius,
                3.0f,
                Mathf.Sin(angle) * LandmarkRadius);

            AddChild(new MeshInstance3D
            {
                Name = $"Landmark{i}",
                Mesh = pillarMesh,
                MaterialOverride = i % 2 == 0 ? cyan : magenta,
                Position = position,
                RotationDegrees = new Vector3(0.0f, Mathf.RadToDeg(angle), 0.0f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    // =====================================================================
    // Arena props (Kenney Car Kit dressing, visual + static collision)
    // =====================================================================

    private void BuildArenaProps()
    {
        // Four clusters of cones / boxes / tires scattered around the pad ring.
        // Static bodies so cars can't drive through them; shadows off to keep it cheap.
        for (int cluster = 0; cluster < 4; cluster++)
        {
            float baseAngle = Mathf.Tau * cluster / 4.0f + 0.35f;
            Vector3 center = new(
                Mathf.Cos(baseAngle) * 34.0f,
                0.0f,
                Mathf.Sin(baseAngle) * 34.0f);

            SpawnProp("cone", center + new Vector3(-1.1f, 0.0f, 0.0f), baseAngle, 1.0f, new Vector3(0.5f, 0.6f, 0.5f));
            SpawnProp("cone", center + new Vector3(1.1f, 0.0f, 0.0f), baseAngle + 0.4f, 1.0f, new Vector3(0.5f, 0.6f, 0.5f));
            SpawnProp("cone-flat", center + new Vector3(0.0f, 0.0f, 1.1f), baseAngle + 0.8f, 1.0f, new Vector3(0.5f, 0.3f, 0.5f));
            SpawnProp("box", center + new Vector3(0.0f, 0.0f, -1.1f), baseAngle + 1.2f, 0.9f, new Vector3(0.75f, 0.75f, 0.75f));
            SpawnProp("debris-tire", center + new Vector3(2.0f, 0.0f, 0.4f), baseAngle + 2.0f, 1.0f, new Vector3(0.7f, 0.6f, 0.7f));
        }
    }

    private void SpawnProp(string modelName, Vector3 position, float yawRadians, float modelScale, Vector3 colliderSize)
    {
        Node3D? model = AssetLibrary.LoadModel(AssetLibrary.CarModel(modelName));
        if (model == null)
        {
            return;
        }

        var body = new StaticBody3D
        {
            Name = $"Prop_{modelName}",
            Position = position,
            RotationDegrees = new Vector3(0.0f, Mathf.RadToDeg(yawRadians), 0.0f),
        };

        body.AddChild(new CollisionShape3D
        {
            Name = "PropShape",
            Shape = new BoxShape3D { Size = colliderSize },
            Position = new Vector3(0.0f, colliderSize.Y * 0.5f, 0.0f),
        });

        model.Scale = Vector3.One * modelScale;
        AssetLibrary.SetShadows(model, false);
        body.AddChild(model);

        AddChild(body);
    }

    // =====================================================================
    // Player
    // =====================================================================

    private void SpawnPlayer()
    {
        Player = new CarController
        {
            Name = "Player",
            DisplayName = "Overdrive Prototype",
            BodyColor = PlayerColor,
            ModelPath = AssetLibrary.CarModel("race-future"),
            // Slight drop so the kinematic body settles cleanly onto the floor.
            Position = new Vector3(0.0f, 0.70f, 0.0f),
        };

        AddChild(Player);

        // Primary weapon hardpoint sits on the nose and points along the car's -Z.
        Player.AddChild(new WeaponMount
        {
            Name = "WeaponMount",
            Position = new Vector3(0.0f, 0.62f, -0.95f),
        });
    }

    // =====================================================================
    // Rival target cars (Phase 2 combat dummies)
    // =====================================================================

    private void SpawnRivals()
    {
        if (RivalCount <= 0)
        {
            return;
        }

        // Parked in a loose arc ahead of the spawn point: they give the cannon and
        // the homing missiles something to test against without any AI yet.
        var colors = new[]
        {
            new Color(1.00f, 0.15f, 0.55f),
            new Color(1.00f, 0.65f, 0.10f),
            new Color(0.55f, 0.25f, 1.00f),
        };

        for (int i = 0; i < RivalCount; i++)
        {
            float side = i % 2 == 0 ? -1.0f : 1.0f;
            float distance = 20.0f + (i / 2) * 14.0f;

            string[] rivalModels = { "police", "sedan-sports", "race" };
            AddChild(new CarController
            {
                Name = $"Rival{i}",
                DisplayName = $"Rival {i + 1}",
                BodyColor = colors[i % colors.Length],
                ModelPath = AssetLibrary.CarModel(rivalModels[i % rivalModels.Length]),
                UsePlayerInput = false, // no driver yet: static target
                Position = new Vector3(side * (10.0f + i * 2.0f), 0.70f, -distance),
                RotationDegrees = new Vector3(0.0f, side * 12.0f, 0.0f),
            });
        }
    }

    // =====================================================================
    // Power-up pads
    // =====================================================================

    private void BuildPowerUpPads()
    {
        for (int i = 0; i < PowerUpPadCount; i++)
        {
            float angle = Mathf.Tau * i / Mathf.Max(PowerUpPadCount, 1);

            AddChild(new PowerUpPad
            {
                Name = $"PowerUpPad{i}",
                UseRandomEffect = true,
                Position = new Vector3(
                    Mathf.Cos(angle) * PowerUpPadRadius,
                    0.0f,
                    Mathf.Sin(angle) * PowerUpPadRadius),
            });
        }
    }

    // =====================================================================
    // Chase camera
    // =====================================================================

    private void BuildCamera()
    {
        if (Player == null)
        {
            return;
        }

        ChaseCamera = new SmoothFollowCamera { Name = "ChaseCamera" };

        // Add first (which runs _Ready), then hand it a target and snap into place.
        AddChild(ChaseCamera);
        ChaseCamera.Target = Player;
        ChaseCamera.SnapToTarget();
    }
}
