using Godot;
using ApexCombatRacing.Vehicles;

namespace ApexCombatRacing.Combat;

/// <summary>
/// Lightweight homing missile.
///
/// * Kinematic <see cref="CharacterBody3D"/> in Floating motion mode — no rigid body,
///   no impulse solver, no collision response to tune.
/// * Homes on the nearest live vehicle from the <c>vehicles</c> group, excluding the
///   shooter. Steering is an exponential lerp of the heading vector, capped by the
///   smoothing factor, so it curves believably without a full guidance model.
/// * Detonates on proximity to the target, on any physical contact, or when its
///   lifetime expires; damage falls off linearly to the edge of the blast radius.
/// * The only "VFX" is a short-lived unshaded sphere scaled by a Tween — cheap enough
///   to be irrelevant on low-end hardware.
/// </summary>
public partial class HomingMissile : CharacterBody3D
{
    /// <summary>Vehicle that fired this missile. It is never damaged by it, and never targeted.</summary>
    public BaseVehicle? OwnerVehicle { get; set; }

    /// <summary>Launch heading. Normalised in _Ready; defaults to -Z (Godot forward).</summary>
    public Vector3 Direction { get; set; } = Vector3.Forward;

    public float Speed { get; set; } = 55.0f;

    /// <summary>Higher = tighter turn. 4–8 feels arcade-correct at this speed.</summary>
    public float TurnSharpness { get; set; } = 5.0f;

    public float Damage { get; set; } = 26.0f;
    public float BlastRadius { get; set; } = 6.5f;
    public float LifetimeSeconds { get; set; } = 6.0f;

    /// <summary>Distance at which the fuse trips when closing on the target.</summary>
    public float ProximityDetonationDistance { get; set; } = 2.2f;

    private BaseVehicle? _target;
    private Vector3 _heading = Vector3.Forward;
    private float _life;
    private bool _detonated;

    // =====================================================================
    // Lifecycle
    // =====================================================================

    public override void _Ready()
    {
        base._Ready();

        // Layer 2 = projectiles, mask 1 = world + vehicles. The 4 ms physics ticks
        // spent on this body are the entire cost of the missile.
        CollisionLayer = 2;
        CollisionMask = 1;

        MotionMode = MotionModeEnum.Floating; // no floor snapping, no gravity assumption
        UpDirection = Vector3.Up;

        AddChild(new CollisionShape3D
        {
            Name = "MissileShape",
            Shape = new SphereShape3D { Radius = 0.35f },
        });

        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.9f, 0.9f, 0.9f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.45f, 0.10f),
            EmissionEnergyMultiplier = 5.0f,
        };

        AddChild(new MeshInstance3D
        {
            Name = "MissileMesh",
            Mesh = new CapsuleMesh
            {
                Radius = 0.12f,
                Height = 0.9f,
                RadialSegments = 6,
                Rings = 2,
                Material = material,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });

        // Never collide with the car that fired us.
        if (OwnerVehicle != null)
        {
            AddCollisionExceptionWith(OwnerVehicle);
        }

        _heading = Direction.LengthSquared() > 0.0001f ? Direction.Normalized() : Vector3.Forward;
        _life = LifetimeSeconds;

        FaceHeading();
    }

    // =====================================================================
    // Guidance
    // =====================================================================

    public override void _PhysicsProcess(double delta)
    {
        if (_detonated)
        {
            return;
        }

        float dt = (float)delta;

        _life -= dt;
        if (_life <= 0.0f)
        {
            Detonate();
            return;
        }

        AcquireTarget();

        if (_target != null)
        {
            Vector3 toTarget = _target.GlobalPosition + Vector3.Up * 0.6f - GlobalPosition;

            if (toTarget.LengthSquared() <= ProximityDetonationDistance * ProximityDetonationDistance)
            {
                Detonate();
                return;
            }

            if (toTarget.LengthSquared() > 0.01f)
            {
                // Exponential steer: frame-rate independent and cheaper than angle math.
                float blend = 1.0f - Mathf.Exp(-TurnSharpness * dt);
                _heading = _heading.Lerp(toTarget.Normalized(), blend).Normalized();
            }
        }

        FaceHeading();

        Velocity = _heading * Speed;
        MoveAndSlide();

        // Physical contact (wall, floor, another car) trips the fuse too.
        if (GetSlideCollisionCount() > 0)
        {
            Detonate();
        }
    }

    /// <summary>Picks the nearest live enemy vehicle once, then keeps it.</summary>
    private void AcquireTarget()
    {
        if (_target != null && IsInstanceValid(_target) && !_target.IsDestroyed)
        {
            return;
        }

        _target = null;
        float bestDistanceSq = float.MaxValue;

        foreach (Node node in GetTree().GetNodesInGroup(BaseVehicle.VehicleGroup))
        {
            if (node is not BaseVehicle candidate || candidate == OwnerVehicle || candidate.IsDestroyed)
            {
                continue;
            }

            float distanceSq = GlobalPosition.DistanceSquaredTo(candidate.GlobalPosition);
            if (distanceSq < bestDistanceSq)
            {
                bestDistanceSq = distanceSq;
                _target = candidate;
            }
        }
    }

    private void FaceHeading()
    {
        if (_heading.LengthSquared() <= 0.0001f)
        {
            return;
        }

        // LookAt fails if the heading is parallel to up; the guard keeps it safe.
        Vector3 lookAt = GlobalPosition + _heading;
        if (Mathf.Abs(_heading.Normalized().Dot(Vector3.Up)) < 0.99f)
        {
            LookAt(lookAt, Vector3.Up);
        }
    }

    // =====================================================================
    // Detonation
    // =====================================================================

    private void Detonate()
    {
        if (_detonated)
        {
            return;
        }

        _detonated = true;

        // Radial damage with linear falloff. Vehicles are a tiny group, so walking it
        // is cheaper than maintaining spatial queries for a single explosion.
        foreach (Node node in GetTree().GetNodesInGroup(BaseVehicle.VehicleGroup))
        {
            if (node is not BaseVehicle vehicle || vehicle == OwnerVehicle || vehicle.IsDestroyed)
            {
                continue;
            }

            float distance = vehicle.GlobalPosition.DistanceTo(GlobalPosition);
            if (distance > BlastRadius)
            {
                continue;
            }

            float falloff = 1.0f - distance / BlastRadius;
            vehicle.TakeDamage(Damage * (0.35f + 0.65f * falloff));
        }

        SpawnFlash();
        QueueFree();
    }

    /// <summary>Single expanding unshaded sphere, then gone. No particles, no lights.</summary>
    private void SpawnFlash()
    {
        Node parent = GetParent() ?? GetTree().Root;

        var flashMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.65f, 0.25f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true,
            Emission = new Color(1.0f, 0.55f, 0.15f),
            EmissionEnergyMultiplier = 3.0f,
        };

        var flash = new MeshInstance3D
        {
            Name = "MissileFlash",
            Mesh = new SphereMesh
            {
                Radius = 0.6f,
                Height = 1.2f,
                RadialSegments = 8,
                Rings = 4,
                Material = flashMaterial,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        // Add first, then place: a node's global transform is only meaningful in-tree.
        parent.AddChild(flash);
        flash.GlobalPosition = GlobalPosition;

        Tween tween = flash.CreateTween();
        tween.TweenProperty(flash, "scale", Vector3.One * BlastRadius * 0.55f, 0.22f);
        tween.TweenCallback(Callable.From(flash.QueueFree));
    }
}
