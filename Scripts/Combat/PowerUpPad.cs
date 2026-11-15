using Godot;
using ApexCombatRacing.Vehicles;

namespace ApexCombatRacing.Combat;

/// <summary>What a pad hands out when a vehicle drives over it.</summary>
public enum PowerUpKind
{
    /// <summary>Instantly tops up the nitro tank (and the boost that comes with it).</summary>
    NitroSurge = 0,

    /// <summary>Restores a fraction of maximum hull health.</summary>
    Repair = 1,

    /// <summary>Loads homing missiles into the vehicle's <see cref="WeaponMount"/>.</summary>
    HomingMissile = 2,
}

/// <summary>
/// Drive-over pickup.
///
/// Built from code (Area3D + cylinder collision + emissive placeholder geometry) so
/// there are no imported assets to break. Detection is a plain Area3D overlap against
/// the vehicle physics layer — no per-frame distance checks, no physics bodies moving.
///
/// Lifecycle: armed -> vehicle overlaps -> effect granted -> 5 s cooldown with the
/// visual hidden and monitoring suspended -> re-armed. Idle cost is one node rotation
/// per physics-free frame; no overlap polling, no distance checks, no allocations.
/// </summary>
[GlobalClass]
public partial class PowerUpPad : Area3D
{
    // ---------------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------------

    [ExportGroup("Pickup")]
    [Export] public bool UseRandomEffect { get; set; } = true;

    /// <summary>Fixed effect when <see cref="UseRandomEffect"/> is false.</summary>
    [Export] public PowerUpKind Effect { get; set; } = PowerUpKind.NitroSurge;

    [Export(PropertyHint.Range, "1,30,0.5")] public float RespawnSeconds { get; set; } = 5.0f;

    /// <summary>Fraction of the vehicle's max health repaired by a Repair pickup.</summary>
    [Export(PropertyHint.Range, "0.05,1,0.05")] public float RepairFraction { get; set; } = 0.35f;

    [Export(PropertyHint.Range, "1,5,1")] public int MissilesGranted { get; set; } = 1;

    [ExportGroup("Shape")]
    [Export(PropertyHint.Range, "0.3,3,0.1")] public float PadRadius { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0.1,1,0.1")] public float PadHeight { get; set; } = 0.2f;

    /// <summary>True while a vehicle can collect from this pad.</summary>
    public bool IsAvailable => _cooldown <= 0.0f;

    // ---------------------------------------------------------------------
    // Runtime
    // ---------------------------------------------------------------------

    private float _cooldown;
    private Node3D _visual = null!;
    private StandardMaterial3D _padMaterial = null!;
    private PowerUpKind _armedKind = PowerUpKind.NitroSurge;

    public override void _Ready()
    {
        base._Ready();

        // Layer 3 (bit 2) = pickups. Mask layer 1 = only vehicles trigger pads.
        CollisionLayer = 4;
        CollisionMask = 1;
        Monitoring = true;
        Monitorable = false; // nothing needs to detect the pad itself

        var shape = new CollisionShape3D
        {
            Name = "PadShape",
            Shape = new CylinderShape3D { Radius = PadRadius, Height = PadHeight },
            Position = new Vector3(0.0f, PadHeight * 0.5f, 0.0f),
        };
        AddChild(shape);

        BuildVisual();

        BodyEntered += OnBodyEntered;

        RollNextKind();
    }

    /// <summary>
    /// Placeholder geometry: an emissive disc on the floor plus a slowly spinning
    /// cube that shows the pad is live. Two draw calls, no particles, no lights.
    /// </summary>
    private void BuildVisual()
    {
        _visual = new Node3D { Name = "Visual" };
        AddChild(_visual);

        _padMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.05f, 0.08f),
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.95f, 1.0f),
            EmissionEnergyMultiplier = 2.5f,
            Roughness = 0.6f,
        };

        _visual.AddChild(new MeshInstance3D
        {
            Name = "PadDisc",
            Mesh = new CylinderMesh
            {
                TopRadius = PadRadius,
                BottomRadius = PadRadius,
                Height = 0.12f,
                RadialSegments = 16,
                Material = _padMaterial,
            },
            Position = new Vector3(0.0f, 0.06f, 0.0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Flat glowing disc only - no floating core cube.
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (_cooldown > 0.0f)
        {
            _cooldown -= dt;
            if (_cooldown <= 0.0f)
            {
                Rearm();
            }
            return;
        }

        // Flat disc: gentle pulse via scale is skipped to keep it cheap.
    }

    // ---------------------------------------------------------------------
    // Pickup logic
    // ---------------------------------------------------------------------

    private void OnBodyEntered(Node3D body)
    {
        if (_cooldown > 0.0f || body is not BaseVehicle vehicle || vehicle.IsDestroyed)
        {
            return;
        }

        Grant(vehicle, _armedKind);
        Consume();
    }

    private void Grant(BaseVehicle vehicle, PowerUpKind kind)
    {
        switch (kind)
        {
            case PowerUpKind.NitroSurge:
                vehicle.AddNitro(vehicle.MaxNitro);
                break;

            case PowerUpKind.Repair:
                vehicle.Repair(vehicle.MaxHealth * RepairFraction);
                break;

            case PowerUpKind.HomingMissile:
                WeaponMount.Find(vehicle)?.GrantMissiles(MissilesGranted);
                break;
        }

        // Small immediate feedback: the pad flashes to full white as it is taken.
        _padMaterial.Emission = Colors.White;
        _padMaterial.EmissionEnergyMultiplier = 4.0f;
    }

    /// <summary>Starts the respawn cycle and takes the pad offline.</summary>
    private void Consume()
    {
        _cooldown = Mathf.Max(RespawnSeconds, 0.1f);
        _visual.Visible = false;

        // Monitoring must not be toggled from inside the overlap callback.
        SetDeferred(Area3D.PropertyName.Monitoring, false);
    }

    private void Rearm()
    {
        RollNextKind();
        _visual.Visible = true;
        Monitoring = true; // safe: called from _Process, not from a physics signal
    }

    /// <summary>Picks the next effect and colours the pad to telegraph it.</summary>
    private void RollNextKind()
    {
        _armedKind = UseRandomEffect
            ? (PowerUpKind)GD.RandRange((int)PowerUpKind.NitroSurge, (int)PowerUpKind.HomingMissile)
            : Effect;

        Color color = KindColor(_armedKind);
        _padMaterial.Emission = color;
        _padMaterial.EmissionEnergyMultiplier = 2.5f;
    }

    private static Color KindColor(PowerUpKind kind) => kind switch
    {
        PowerUpKind.NitroSurge => new Color(0.05f, 0.95f, 1.00f),   // cyan
        PowerUpKind.Repair => new Color(0.20f, 1.00f, 0.35f),       // green
        PowerUpKind.HomingMissile => new Color(1.00f, 0.35f, 0.10f), // orange
        _ => Colors.White,
    };
}
