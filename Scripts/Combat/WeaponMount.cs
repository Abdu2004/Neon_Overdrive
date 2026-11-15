using Godot;
using ApexCombatRacing.Vehicles;

namespace ApexCombatRacing.Combat;

/// <summary>
/// Weapon hardpoint for a vehicle: a hitscan autocannon plus a small missile rack.
///
/// * <b>Cannon</b>: a single <see cref="RayCast3D"/> fired manually (<c>ForceRaycastUpdate</c>)
///   only on the frames the trigger is down — idle raycasts cost nothing. Shots add heat;
///   heat cools constantly; hitting 100% locks the trigger for a couple of seconds.
///   No bullets, no rigid bodies, no per-projectile nodes: one ray per shot.
/// * <b>Missiles</b>: fed by <see cref="PowerUpPad"/> homing-missile pickups and launched
///   with the secondary fire action. They live in <see cref="HomingMissile"/>.
///
/// Attach as a child of any <see cref="BaseVehicle"/>. Aim is the mount's own -Z axis,
/// so rotating the mount aims the gun.
/// </summary>
[GlobalClass]
public partial class WeaponMount : Node3D
{
    // ---------------------------------------------------------------------
    // Cannon tuning
    // ---------------------------------------------------------------------

    [ExportGroup("Cannon")]
    [Export(PropertyHint.Range, "0,100,0.5")] public float DamagePerShot { get; set; } = 9.0f;
    [Export(PropertyHint.Range, "0.5,30,0.5")] public float ShotsPerSecond { get; set; } = 5.0f;
    [Export(PropertyHint.Range, "5,500,1")] public float Range { get; set; } = 140.0f;

    /// <summary>Heat added per shot, 0..1 of the heat bar.</summary>
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float HeatPerShot { get; set; } = 0.16f;

    /// <summary>Heat removed per second while the trigger is released.</summary>
    [Export(PropertyHint.Range, "0.05,3,0.05")] public float HeatCoolPerSecond { get; set; } = 0.55f;

    /// <summary>How long the gun stays locked after reaching 100% heat.</summary>
    [Export(PropertyHint.Range, "0.2,10,0.1")] public float OverheatLockSeconds { get; set; } = 2.0f;

    // ---------------------------------------------------------------------
    // Missile tuning
    // ---------------------------------------------------------------------

    [ExportGroup("Missiles")]
    [Export(PropertyHint.Range, "0,6,1")] public int MaxMissileAmmo { get; set; } = 3;
    [Export(PropertyHint.Range, "10,120,1")] public float MissileSpeed { get; set; } = 55.0f;
    [Export(PropertyHint.Range, "1,100,1")] public float MissileDamage { get; set; } = 26.0f;
    [Export(PropertyHint.Range, "1,20,0.5")] public float MissileBlastRadius { get; set; } = 6.5f;

    [Export] public bool UsePlayerInput { get; set; } = true;

    // ---------------------------------------------------------------------
    // Read-only state (HUD in a later phase)
    // ---------------------------------------------------------------------

    /// <summary>0..1 cannon heat.</summary>
    public float Heat { get; private set; }

    public bool IsOverheated => _overheatTimer > 0.0f;
    public int MissileAmmo { get; private set; }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private BaseVehicle _vehicle = null!;
    private RayCast3D _ray = null!;
    private MeshInstance3D _muzzleFlash = null!;
    private StandardMaterial3D _muzzleMaterial = null!;

    private float _shotCooldown;
    private float _overheatTimer;
    private float _flashTimer;

    // =====================================================================
    // Lifecycle
    // =====================================================================

    public override void _Ready()
    {
        base._Ready();

        if (GetParent() is not BaseVehicle vehicle)
        {
            GD.PushWarning("WeaponMount must be a child of a BaseVehicle; weapon disabled.");
            SetPhysicsProcess(false);
            return;
        }

        _vehicle = vehicle;

        // Manual raycasting: disabled by default so it costs nothing until we fire.
        _ray = new RayCast3D
        {
            Name = "CannonRay",
            TargetPosition = new Vector3(0.0f, 0.0f, -Range),
            CollisionMask = 1,   // vehicles + world
            Enabled = false,
            HitFromInside = false,
        };
        AddChild(_ray);

        // Never shoot ourselves: our own hull is inside the muzzle's reach.
        _ray.AddException(vehicle);

        BuildVisual();
    }

    private void BuildVisual()
    {
        var barrelMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.12f, 0.12f, 0.15f),
            Metallic = 0.7f,
            Roughness = 0.35f,
            EmissionEnabled = true,
            Emission = new Color(0.05f, 0.75f, 1.0f),
            EmissionEnergyMultiplier = 0.8f,
        };

        AddChild(new MeshInstance3D
        {
            Name = "Barrel",
            Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.16f, 1.1f), Material = barrelMaterial },
            Position = new Vector3(0.0f, 0.0f, -0.55f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Muzzle flash placeholder: one small unshaded sphere toggled for ~50 ms.
        _muzzleMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.85f, 0.4f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.7f, 0.2f),
            EmissionEnergyMultiplier = 4.0f,
        };

        _muzzleFlash = new MeshInstance3D
        {
            Name = "MuzzleFlash",
            Mesh = new SphereMesh
            {
                Radius = 0.14f,
                Height = 0.28f,
                RadialSegments = 6,
                Rings = 3,
                Material = _muzzleMaterial,
            },
            Position = new Vector3(0.0f, 0.0f, -1.15f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_muzzleFlash);
    }

    // =====================================================================
    // Input + heat
    // =====================================================================

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        UpdateHeat(dt);

        if (_vehicle.IsDestroyed || !UsePlayerInput)
        {
            return;
        }

        if (Input.IsActionPressed("fire_primary"))
        {
            TryFireCannon();
        }

        if (Input.IsActionJustPressed("fire_secondary"))
        {
            TryFireMissile();
        }
    }

    private void UpdateHeat(float delta)
    {
        if (_shotCooldown > 0.0f)
        {
            _shotCooldown = Mathf.Max(0.0f, _shotCooldown - delta);
        }

        if (_overheatTimer > 0.0f)
        {
            _overheatTimer = Mathf.Max(0.0f, _overheatTimer - delta);
        }

        // Continuous cooling keeps the loop stateless: no "is firing" flag needed.
        if (Heat > 0.0f)
        {
            Heat = Mathf.Max(0.0f, Heat - HeatCoolPerSecond * delta);
        }

        if (_flashTimer > 0.0f)
        {
            _flashTimer -= delta;
            if (_flashTimer <= 0.0f)
            {
                _muzzleFlash.Visible = false;
            }
        }
    }

    // =====================================================================
    // Cannon
    // =====================================================================

    private void TryFireCannon()
    {
        if (_shotCooldown > 0.0f || IsOverheated)
        {
            return;
        }

        _shotCooldown = 1.0f / Mathf.Max(ShotsPerSecond, 0.1f);

        Heat = Mathf.Min(1.0f, Heat + HeatPerShot);
        if (Heat >= 1.0f)
        {
            _overheatTimer = OverheatLockSeconds;
        }

        // Single synchronous ray: no projectile node, zero allocations.
        _ray.ForceRaycastUpdate();
        if (_ray.IsColliding() && _ray.GetCollider() is BaseVehicle target && target != _vehicle)
        {
            target.TakeDamage(DamagePerShot);
        }

        _muzzleFlash.Visible = true;
        _flashTimer = 0.05f;
    }

    // =====================================================================
    // Missiles
    // =====================================================================

    /// <summary>Loads missile ammo (called by pickup pads).</summary>
    public void GrantMissiles(int count)
    {
        MissileAmmo = Mathf.Clamp(MissileAmmo + count, 0, MaxMissileAmmo);
    }

    private void TryFireMissile()
    {
        if (MissileAmmo <= 0)
        {
            return;
        }

        MissileAmmo--;

        // Missiles are parented to the arena (current scene), not the car, so they
        // keep their own world transform once launched.
        Node launchParent = GetTree().CurrentScene ?? GetTree().Root;

        var missile = new HomingMissile
        {
            Name = "HomingMissile",
            OwnerVehicle = _vehicle,
            Speed = MissileSpeed,
            Damage = MissileDamage,
            BlastRadius = MissileBlastRadius,
            Direction = -GlobalTransform.Basis.Z,
        };

        launchParent.AddChild(missile);
        missile.GlobalPosition = _muzzleFlash.GlobalPosition;
    }

    // =====================================================================
    // Lookup helper
    // =====================================================================

    /// <summary>
    /// Finds the weapon mount on a vehicle by checking its direct children.
    /// Avoids a GetNode path dependency so the mount can be named freely.
    /// </summary>
    public static WeaponMount? Find(Node vehicle)
    {
        foreach (Node child in vehicle.GetChildren())
        {
            if (child is WeaponMount mount)
            {
                return mount;
            }
        }

        return null;
    }
}
