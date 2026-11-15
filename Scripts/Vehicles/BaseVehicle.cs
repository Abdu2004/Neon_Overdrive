using Godot;
using ApexCombatRacing.Utils;
using System.Collections.Generic;

namespace ApexCombatRacing.Vehicles;

/// <summary>
/// High level handling mode of a vehicle. Combat/UI code reads this instead of
/// poking at the controller's internals.
/// </summary>
public enum VehicleDriftState
{
    /// <summary>Normal driving: lateral velocity is scrubbed quickly (high grip).</summary>
    Grip = 0,

    /// <summary>Handbrake held above the drift speed threshold, charge is accumulating.</summary>
    Drifting = 1,

    /// <summary>Drift released with enough charge: temporary speed boost is active.</summary>
    DriftBoost = 2,

    /// <summary>No wheels on the floor; steering authority is heavily reduced.</summary>
    Airborne = 3,

    /// <summary>Health reached zero; the chassis no longer accepts driving input.</summary>
    Disabled = 4,
}

/// <summary>
/// Shared, engine-level state for every drivable vehicle.
///
/// Design notes:
/// * We derive from <see cref="CharacterBody3D"/> on purpose: it is a kinematic
///   body driven by <c>MoveAndSlide()</c>. No physics joints, no soft constraints,
///   no solver cost — which is exactly what "arcade on a low-end PC" needs.
/// * This class owns survival stats (health/armor/nitro) and the physics loop
///   skeleton. Subclasses only implement <see cref="SimulateVehicle"/> (how input
///   becomes velocity), <see cref="PostMove"/> (collision reactions) and can
///   override <see cref="BuildDefaultBody"/> to swap the placeholder mesh.
/// * All tuning values are exported so designers can tweak without recompiling.
/// </summary>
public abstract partial class BaseVehicle : CharacterBody3D
{
    /// <summary>
    /// Group every vehicle joins on <c>_Ready</c>. Combat code uses it instead of
    /// walking the scene tree, so target search stays O(vehicles) with no allocation.
    /// </summary>
    public const string VehicleGroup = "vehicles";

    // ---------------------------------------------------------------------
    // Signals (consumed by HUD / combat systems in later phases)
    // ---------------------------------------------------------------------

    [Signal] public delegate void HealthChangedEventHandler(float health, float armor);
    [Signal] public delegate void NitroChangedEventHandler(float nitro);
    [Signal] public delegate void DriftStateChangedEventHandler(int newState);
    [Signal] public delegate void VehicleDestroyedEventHandler();

    // ---------------------------------------------------------------------
    // Identity / visuals
    // ---------------------------------------------------------------------

    [ExportGroup("Identity")]
    [Export] public string DisplayName { get; set; } = "Prototype";
    [Export] public Color BodyColor { get; set; } = new Color(0.10f, 0.85f, 1.00f);

    /// <summary>
    /// Path to a Kenney car-kit <c>.glb</c> to use as the chassis visual. Leave empty
    /// to fall back to the placeholder box. Tinted with <see cref="BodyColor"/>.
    /// </summary>
    [ExportGroup("Model")]
    [Export] public string ModelPath { get; set; } = "";

    /// <summary>
    /// Kenney cars face +Z while Godot forward is -Z, so the model is yawed this
    /// many degrees on attach. 180 deg for the stock Car Kit.
    /// </summary>
    [Export(PropertyHint.Range, "-180,180,1")] public float ModelYawDegrees { get; set; } = 180.0f;

    /// <summary>Uniform scale applied to the imported model.</summary>
    [Export(PropertyHint.Range, "0.2,4,0.01")] public float ModelScale { get; set; } = 0.82f;

    /// <summary>How strongly <see cref="BodyColor"/> tints the model's body mesh (0..1).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float ModelTintStrength { get; set; } = 0.35f;

    /// <summary>
    /// When true a cheap box chassis + four static wheels are generated when no
    /// model could be loaded (or <see cref="ModelPath"/> is empty). Keeps the
    /// project runnable even if the art pack is missing.
    /// </summary>
    [Export] public bool BuildPlaceholderMesh { get; set; } = true;

    /// <summary>Max front-wheel visual steer angle in degrees (visual only, no physics).</summary>
    protected const float MaxVisualSteerDegrees = 28.0f;

    /// <summary>Known wheel radius of the Kenney wheel models in metres, pre-scale.</summary>
    protected const float WheelRadiusMeters = 0.30f;

    // Wheel nodes collected from the loaded model (empty for the placeholder).
    private readonly List<Node3D> _frontWheels = new();
    private readonly List<Node3D> _rearWheels = new();
    private float _wheelSpinAngle;

    // ---------------------------------------------------------------------
    // Performance envelope (metres / second internally, km/h for UI)
    // ---------------------------------------------------------------------

    [ExportGroup("Performance")]
    [Export(PropertyHint.Range, "5,120,0.5")] public float MaxSpeed { get; set; } = 52.0f;
    [Export(PropertyHint.Range, "1,60,0.5")] public float MaxReverseSpeed { get; set; } = 12.0f;
    [Export(PropertyHint.Range, "0.5,4,0.05")] public float NitroSpeedMultiplier { get; set; } = 1.35f;

    public float MaxSpeedKph => MaxSpeed * 3.6f;

    // ---------------------------------------------------------------------
    // Durability
    // ---------------------------------------------------------------------

    [ExportGroup("Durability")]
    [Export(PropertyHint.Range, "1,1000,1")] public float MaxHealth { get; set; } = 100.0f;
    [Export(PropertyHint.Range, "0,1000,1")] public float MaxArmor { get; set; } = 50.0f;

    /// <summary>Fraction of incoming damage the armor plate absorbs while it lasts.</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float ArmorAbsorption { get; set; } = 0.5f;

    // ---------------------------------------------------------------------
    // Nitro
    // ---------------------------------------------------------------------

    [ExportGroup("Nitro")]
    [Export(PropertyHint.Range, "10,300,1")] public float MaxNitro { get; set; } = 100.0f;
    [Export(PropertyHint.Range, "0,100,0.5")] public float NitroRechargePerSecond { get; set; } = 4.0f;

    // ---------------------------------------------------------------------
    // Runtime state
    // ---------------------------------------------------------------------

    public float Health { get; private set; }
    public float Armor { get; private set; }
    public float Nitro { get; private set; }

    public bool IsDestroyed => Health <= 0.0f;

    /// <summary>Signed forward speed in m/s (negative when reversing).</summary>
    public float ForwardSpeed { get; protected set; }

    /// <summary>Absolute speed presented to HUDs, in km/h.</summary>
    public float SpeedKph => Mathf.Abs(ForwardSpeed) * 3.6f;

    public VehicleDriftState DriftState { get; private set; } = VehicleDriftState.Grip;

    /// <summary>Horizontal part of the world velocity — used by cameras and AI.</summary>
    public Vector3 PlanarVelocity => new(Velocity.X, 0.0f, Velocity.Z);

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    public override void _Ready()
    {
        base._Ready();

        AddToGroup(VehicleGroup);

        // CharacterBody3D configuration (see https://docs.godotengine.org/en/stable/classes/class_characterbody3d.html).
        // Grounded mode lets MoveAndSlide() do the slope/step work for us with zero solver cost.
        MotionMode = MotionModeEnum.Grounded;
        UpDirection = Vector3.Up;
        FloorMaxAngle = Mathf.DegToRad(55.0f); // arcades are forgiving on ramps
        FloorSnapLength = 0.6f;                // keeps the car glued over small bumps
        FloorConstantSpeed = false;            // ramps genuinely slow you down
        SlideOnCeiling = false;

        Health = MaxHealth;
        Armor = MaxArmor;
        Nitro = MaxNitro;
        ForwardSpeed = 0.0f;

        BuildDefaultBody();
    }

    /// <summary>
    /// Physics tick: subclasses compute a velocity, then the kinematic body sweeps it.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;

        if (IsDestroyed)
        {
            UpdateDestroyed(dt);
            return;
        }

        SimulateVehicle(dt);
        MoveAndSlide();
        PostMove(dt);
    }

    // ---------------------------------------------------------------------
    // Hooks for subclasses
    // ---------------------------------------------------------------------

    /// <summary>Turn the current input state into <see cref="CharacterBody3D.Velocity"/>.</summary>
    protected abstract void SimulateVehicle(float delta);

    /// <summary>Called after <c>MoveAndSlide()</c> so subclasses can react to impacts.</summary>
    protected virtual void PostMove(float delta) { }

    /// <summary>Called once per tick while the chassis is wrecked (wrecks coast to a stop).</summary>
    protected virtual void UpdateDestroyed(float delta)
    {
        Velocity = Velocity.MoveToward(Vector3.Zero, 18.0f * delta);
        MoveAndSlide();
    }

    /// <summary>Optional visual/combat reaction when damage lands.</summary>
    protected virtual void OnDamaged(float amount) { }

    /// <summary>Build the kinematic collision box that every vehicle uses.</summary>
    protected virtual void BuildCollision()
    {
        if (HasNode("BodyCollision"))
        {
            return;
        }

        var collision = new CollisionShape3D
        {
            Name = "BodyCollision",
            Shape = new BoxShape3D { Size = new Vector3(1.20f, 0.50f, 2.40f) },
            Position = new Vector3(0.0f, 0.25f, 0.0f),
        };
        AddChild(collision);
    }

    /// <summary>
    /// Prototype geometry: load the Kenney <c>.glb</c> if <see cref="ModelPath"/> is
    /// set and resolvable, otherwise fall back to the box chassis so the project
    /// never hard-depends on imported art.
    /// </summary>
    protected virtual void BuildDefaultBody()
    {
        BuildCollision();

        if (!TryAttachModel())
        {
            if (BuildPlaceholderMesh)
            {
                BuildPlaceholderVisual();
            }
        }
    }

    /// <summary>
    /// Try to instance <see cref="ModelPath"/>. Returns true on success and records
    /// the wheel nodes found in the model.
    /// </summary>
    protected bool TryAttachModel()
    {
        if (string.IsNullOrEmpty(ModelPath))
        {
            return false;
        }

        Node3D? model = AssetLibrary.LoadModel(ModelPath);
        if (model == null)
        {
            return false;
        }

        model.Name = "Model";
        model.Position = Vector3.Zero;
        model.RotationDegrees = new Vector3(0.0f, ModelYawDegrees, 0.0f);
        model.Scale = Vector3.One * ModelScale;
        AssetLibrary.TintBody(model, BodyColor, ModelTintStrength);
        AddChild(model);

        CollectWheels(model);
        return true;
    }

    private void CollectWheels(Node3D model)
    {
        foreach (Node node in model.FindChildren("*", "Node3D", true, false))
        {
            if (node is not Node3D part)
            {
                continue;
            }

            string name = part.Name.ToString().ToLowerInvariant();
            if (!name.StartsWith("wheel"))
            {
                continue;
            }

            if (name.Contains("front"))
            {
                _frontWheels.Add(part);
            }
            else if (name.Contains("back") || name.Contains("rear"))
            {
                _rearWheels.Add(part);
            }
        }
    }

    /// <summary>
    /// Spin/steer the wheels collected from the loaded model. Called by subclasses
    /// each physics tick. Safe to call with no wheels (placeholder body).
    /// </summary>
    protected void UpdateWheelVisuals(float forwardSpeed, float steerInput, float delta)
    {
        if (_frontWheels.Count == 0 && _rearWheels.Count == 0)
        {
            return;
        }

        float radius = Mathf.Max(0.05f, WheelRadiusMeters * ModelScale);
        _wheelSpinAngle = Mathf.Wrap(_wheelSpinAngle + forwardSpeed / radius * delta, 0.0f, Mathf.Tau);

        float steerAngle = -steerInput * Mathf.DegToRad(MaxVisualSteerDegrees);

        foreach (Node3D wheel in _frontWheels)
        {
            wheel.Rotation = new Vector3(_wheelSpinAngle, steerAngle, 0.0f);
        }

        foreach (Node3D wheel in _rearWheels)
        {
            wheel.Rotation = new Vector3(_wheelSpinAngle, 0.0f, 0.0f);
        }
    }

    /// <summary>Original placeholder body, kept as an asset-free fallback.</summary>
    protected virtual void BuildPlaceholderVisual()
    {
        // Painted chassis.
        var paint = new StandardMaterial3D
        {
            AlbedoColor = BodyColor,
            Metallic = 0.35f,
            Roughness = 0.40f,
            EmissionEnabled = true,
            Emission = BodyColor,
            EmissionEnergyMultiplier = 0.25f,
        };

        var chassis = new MeshInstance3D
        {
            Name = "ChassisMesh",
            Mesh = new BoxMesh { Size = new Vector3(1.20f, 0.42f, 2.40f), Material = paint },
            Position = new Vector3(0.0f, 0.28f, 0.0f),
        };
        AddChild(chassis);

        // Four static wheels. Deliberately not spinning: the silhouette reads fine and
        // it saves four per-frame transforms on weak hardware.
        var rubber = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.05f, 0.05f, 0.06f),
            Roughness = 0.9f,
        };
        var wheelMesh = new CylinderMesh
        {
            TopRadius = 0.30f,
            BottomRadius = 0.30f,
            Height = 0.22f,
            Material = rubber,
        };

        Vector3[] offsets =
        {
            new(-0.55f, 0.06f, 0.80f),
            new(0.55f, 0.06f, 0.80f),
            new(-0.55f, 0.06f, -0.80f),
            new(0.55f, 0.06f, -0.80f),
        };

        for (int i = 0; i < offsets.Length; i++)
        {
            AddChild(new MeshInstance3D
            {
                Name = $"Wheel{i}",
                Mesh = wheelMesh,
                Position = offsets[i],
                RotationDegrees = new Vector3(0.0f, 0.0f, 90.0f), // cylinder axis -> X
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }
    }

    // ---------------------------------------------------------------------
    // Public combat/stats API
    // ---------------------------------------------------------------------

    /// <summary>
    /// Applies damage through the armor plate first, then to the hull.
    /// </summary>
    public void TakeDamage(float amount)
    {
        if (amount <= 0.0f || IsDestroyed)
        {
            return;
        }

        if (Armor > 0.0f)
        {
            float absorbed = Mathf.Min(Armor, amount * ArmorAbsorption);
            Armor -= absorbed;
            amount -= absorbed;
        }

        Health = Mathf.Max(0.0f, Health - amount);
        EmitSignal(SignalName.HealthChanged, Health, Armor);
        OnDamaged(amount);

        if (Health <= 0.0f)
        {
            SetDriftState(VehicleDriftState.Disabled);
            EmitSignal(SignalName.VehicleDestroyed);
        }
    }

    public void Repair(float amount)
    {
        Health = Mathf.Min(MaxHealth, Health + amount);
        EmitSignal(SignalName.HealthChanged, Health, Armor);
    }

    public void RechargeArmor(float amount)
    {
        Armor = Mathf.Min(MaxArmor, Armor + amount);
        EmitSignal(SignalName.HealthChanged, Health, Armor);
    }

    public void AddNitro(float amount)
    {
        Nitro = Mathf.Clamp(Nitro + amount, 0.0f, MaxNitro);
        EmitSignal(SignalName.NitroChanged, Nitro);
    }

    /// <summary>Spends nitro; returns false once the tank is empty.</summary>
    protected bool DrainNitro(float amount)
    {
        if (Nitro <= 0.0f)
        {
            return false;
        }

        Nitro = Mathf.Max(0.0f, Nitro - amount);
        return Nitro > 0.0f;
    }

    /// <summary>
    /// Continuous nitro trickle (e.g. passive regen). Deliberately does not emit
    /// <c>NitroChanged</c> — HUDs poll <see cref="Nitro"/> each frame, the signal is
    /// reserved for discrete pickups so we are not firing it 60 times a second.
    /// </summary>
    protected void RegenNitro(float amount)
    {
        if (amount <= 0.0f)
        {
            return;
        }

        Nitro = Mathf.Min(MaxNitro, Nitro + amount);
    }

    /// <summary>Changes handling state and notifies listeners only on real transitions.</summary>
    protected void SetDriftState(VehicleDriftState state)
    {
        if (DriftState == state)
        {
            return;
        }

        DriftState = state;
        EmitSignal(SignalName.DriftStateChanged, (int)state);
    }
}
