using Godot;

namespace ApexCombatRacing.Vehicles;

/// <summary>
/// Arcade car handling in the vein of Need for Speed / Blur.
///
/// The model is intentionally *not* a tyre simulation. Each physics tick we:
///   1. read input (overridable so AI can drive the same car),
///   2. resolve the handling state (grip / drift / boost / airborne),
///   3. integrate a single longitudinal speed scalar (throttle, brake, drag, nitro),
///   4. rotate the chassis with speed-sensitive, smoothed steering,
///   5. re-compose world velocity from forward speed + decaying lateral slip + gravity.
///
/// Because we rotate the body *before* re-projecting the old world velocity, the
/// lateral component appears naturally: low grip keeps it (drift), high grip erases
/// it (grip driving). One scalar + one vector per tick — no joints, no raycasts.
/// </summary>
[GlobalClass]
public partial class CarController : BaseVehicle
{
    // ---------------------------------------------------------------------
    // Input
    // ---------------------------------------------------------------------

    /// <summary>Disable to drive this car from code (AI, replay, cutscene).</summary>
    [ExportGroup("Input")]
    [Export]
    public bool UsePlayerInput { get; set; } = true;

    /// <summary>Exponential smoothing applied to steering so digital keys feel analogue.</summary>
    [Export(PropertyHint.Range, "1,40,0.5")] public float SteerSmoothing { get; set; } = 14.0f;

    // Public so an AI driver / replay system can write them directly while
    // UsePlayerInput is false (ReadInput is skipped in that case).
    public float ThrottleInput { get; set; }
    public float BrakeInput { get; set; }
    public float SteerInput { get; set; }
    public float HandbrakeInput { get; set; }
    public float NitroInput { get; set; }

    // ---------------------------------------------------------------------
    // Engine
    // ---------------------------------------------------------------------

    [ExportGroup("Engine")]
    [Export(PropertyHint.Range, "5,80,0.5")] public float EngineAcceleration { get; set; } = 26.0f;
    /// <summary>How much acceleration fades as the car approaches top speed (0..1).</summary>
    [Export(PropertyHint.Range, "0,1,0.05")] public float PowerFalloff { get; set; } = 0.78f;
    [Export(PropertyHint.Range, "5,120,0.5")] public float BrakeDeceleration { get; set; } = 40.0f;
    [Export(PropertyHint.Range, "5,60,0.5")] public float ReverseAcceleration { get; set; } = 16.0f;
    [Export(PropertyHint.Range, "0,20,0.5")] public float RollingResistance { get; set; } = 4.0f;
    [Export(PropertyHint.Range, "0,0.02,0.0005")] public float AirDrag { get; set; } = 0.0015f;

    // ---------------------------------------------------------------------
    // Steering & grip
    // ---------------------------------------------------------------------

    [ExportGroup("Steering & Grip")]
    [Export(PropertyHint.Range, "20,220,1")] public float BaseTurnRateDegrees { get; set; } = 105.0f;
    /// <summary>Fraction of turn rate still available at top speed (prevents spin-outs).</summary>
    [Export(PropertyHint.Range, "0.1,1,0.05")] public float HighSpeedSteerFactor { get; set; } = 0.45f;
    /// <summary>Lateral velocity damping while gripping. Higher = more "on rails".</summary>
    [Export(PropertyHint.Range, "1,30,0.5")] public float GripLateralDamping { get; set; } = 9.0f;
    /// <summary>Lateral velocity damping while drifting. Lower = longer slides.</summary>
    [Export(PropertyHint.Range, "0.2,10,0.1")] public float DriftLateralDamping { get; set; } = 1.4f;
    [Export(PropertyHint.Range, "1,3,0.05")] public float DriftYawMultiplier { get; set; } = 1.65f;
    [Export(PropertyHint.Range, "2,40,0.5")] public float DriftMinSpeed { get; set; } = 10.0f;
    /// <summary>Extra outward slide fed in while drifting, for that Blur-style kick.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float DriftSlideFactor { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float DriftSpeedScrub { get; set; } = 0.16f;

    // ---------------------------------------------------------------------
    // Drift boost
    // ---------------------------------------------------------------------

    [ExportGroup("Drift Boost")]
    /// <summary>Charge (0..1) required to earn a boost when releasing a drift.</summary>
    [Export(PropertyHint.Range, "0.1,1,0.05")] public float DriftChargeThreshold { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "0.1,3,0.05")] public float DriftChargePerSecond { get; set; } = 0.75f;
    [Export(PropertyHint.Range, "0,5,0.05")] public float DriftChargeDecayPerSecond { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "0,30,0.5")] public float DriftBoostImpulse { get; set; } = 9.0f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float DriftBoostDuration { get; set; } = 1.2f;
    [Export(PropertyHint.Range, "1,2,0.01")] public float DriftBoostSpeedMultiplier { get; set; } = 1.15f;

    // ---------------------------------------------------------------------
    // Nitro
    // ---------------------------------------------------------------------

    [ExportGroup("Nitro")]
    [Export(PropertyHint.Range, "0,80,0.5")] public float NitroAcceleration { get; set; } = 22.0f;
    [Export(PropertyHint.Range, "0,100,0.5")] public float NitroDrainPerSecond { get; set; } = 30.0f;

    // ---------------------------------------------------------------------
    // Vertical
    // ---------------------------------------------------------------------

    [ExportGroup("Vertical")]
    [Export(PropertyHint.Range, "5,60,0.5")] public float Gravity { get; set; } = 22.0f;
    /// <summary>Small downward bias that keeps MoveAndSlide grounded on crests.</summary>
    [Export(PropertyHint.Range, "0,10,0.1")] public float GroundStickForce { get; set; } = 2.0f;

    // ---------------------------------------------------------------------
    // Runtime state (read by HUD / VFX in later phases)
    // ---------------------------------------------------------------------

    /// <summary>0..1 drift charge. Crossing <see cref="DriftChargeThreshold"/> pays a boost.</summary>
    public float DriftCharge { get; private set; }

    public bool IsDriftBoosting => _boostTimer > 0.0f;
    public bool IsNitroActive { get; private set; }

    private float _boostTimer;
    private float _smoothedSteer;
    private bool _wasDrifting;

    // =====================================================================
    // Physics
    // =====================================================================

    protected override void SimulateVehicle(float delta)
    {
        ReadInput(delta);

        // Steering is smoothed centrally so AI input benefits from it too.
        // Digital keys are 0/1; the exponential ramp makes them feel analogue.
        _smoothedSteer = Mathf.Lerp(_smoothedSteer, SteerInput, 1.0f - Mathf.Exp(-SteerSmoothing * delta));

        UpdateHandlingState();
        UpdateBoostAndNitro(delta);
        UpdateLongitudinalSpeed(delta);
        UpdateSteering(delta);
        ComposeVelocity(delta);
        UpdateDriftCharge(delta);

        // Visual-only wheel spin / steer. Driven by the same kinematics as the body.
        UpdateWheelVisuals(ForwardSpeed, _smoothedSteer, delta);
    }

    /// <summary>
    /// Called once per tick while the car is wrecked: keep drifting to a halt.
    /// </summary>
    protected override void UpdateDestroyed(float delta)
    {
        ForwardSpeed = Mathf.MoveToward(ForwardSpeed, 0.0f, 20.0f * delta);
        Velocity = Velocity.MoveToward(Vector3.Zero, 22.0f * delta);
        MoveAndSlide();
        UpdateWheelVisuals(ForwardSpeed, 0.0f, delta);
    }

    /// <summary>
    /// Reads the InputMap actions. Override (or set <see cref="UsePlayerInput"/> to false
    /// and drive the public input properties) to hand the car to an AI driver.
    /// </summary>
    protected virtual void ReadInput(float delta)
    {
        if (!UsePlayerInput)
        {
            return;
        }

        ThrottleInput = Input.GetActionStrength("accelerate");
        BrakeInput = Input.GetActionStrength("brake");
        HandbrakeInput = Input.GetActionStrength("handbrake_drift");
        NitroInput = Input.GetActionStrength("nitro_boost");

        // -1 = full left, +1 = full right. Analog axes work for free via GetAxis.
        SteerInput = Input.GetAxis("steer_left", "steer_right");
    }

    // ---------------------------------------------------------------------
    // State machine
    // ---------------------------------------------------------------------

    private void UpdateHandlingState()
    {
        if (_boostTimer > 0.0f)
        {
            SetDriftState(VehicleDriftState.DriftBoost);
            return;
        }

        if (!IsOnFloor())
        {
            SetDriftState(VehicleDriftState.Airborne);
            return;
        }

        bool wantsDrift = HandbrakeInput > 0.1f && Mathf.Abs(ForwardSpeed) > DriftMinSpeed;
        SetDriftState(wantsDrift ? VehicleDriftState.Drifting : VehicleDriftState.Grip);
    }

    private void UpdateBoostAndNitro(float delta)
    {
        if (_boostTimer > 0.0f)
        {
            _boostTimer = Mathf.Max(0.0f, _boostTimer - delta);
        }

        bool wantsNitro = NitroInput > 0.1f && Nitro > 0.0f && ForwardSpeed > 1.0f;
        IsNitroActive = wantsNitro;

        if (IsNitroActive)
        {
            DrainNitro(NitroDrainPerSecond * delta);
        }
        else if (NitroRechargePerSecond > 0.0f && Nitro < MaxNitro)
        {
            // Regen is silent: HUDs poll Nitro each frame, signals are for discrete events.
            RegenNitro(NitroRechargePerSecond * delta);
        }
    }

    // ---------------------------------------------------------------------
    // Longitudinal integration
    // ---------------------------------------------------------------------

    private void UpdateLongitudinalSpeed(float delta)
    {
        // Speed ceiling for this tick (nitro and drift boost both raise it).
        float topSpeed = MaxSpeed;
        if (IsNitroActive)
        {
            topSpeed *= NitroSpeedMultiplier;
        }
        if (_boostTimer > 0.0f)
        {
            topSpeed = Mathf.Max(topSpeed, MaxSpeed * DriftBoostSpeedMultiplier);
        }

        if (ThrottleInput > 0.01f)
        {
            if (ForwardSpeed < -0.1f)
            {
                // Braking out of a reverse roll.
                ForwardSpeed = Mathf.MoveToward(ForwardSpeed, 0.0f, BrakeDeceleration * ThrottleInput * delta);
            }
            else
            {
                // Power curve: punchy low end, tapering so top speed feels earned.
                float ratio = Mathf.Clamp(ForwardSpeed / Mathf.Max(topSpeed, 1.0f), 0.0f, 1.0f);
                float accel = EngineAcceleration * (1.0f - PowerFalloff * ratio);
                if (IsNitroActive)
                {
                    accel += NitroAcceleration;
                }
                ForwardSpeed += accel * ThrottleInput * delta;
            }
        }

        if (BrakeInput > 0.01f)
        {
            if (ForwardSpeed > 0.1f)
            {
                ForwardSpeed = Mathf.MoveToward(ForwardSpeed, 0.0f, BrakeDeceleration * BrakeInput * delta);
            }
            else
            {
                ForwardSpeed = Mathf.MoveToward(ForwardSpeed, -MaxReverseSpeed, ReverseAcceleration * BrakeInput * delta);
            }
        }

        if (ThrottleInput < 0.01f && BrakeInput < 0.01f)
        {
            // Coasting: gentle engine braking, never an instant stop.
            ForwardSpeed = Mathf.MoveToward(ForwardSpeed, 0.0f, RollingResistance * delta);
        }

        // Aerodynamic drag, only meaningful at high speed.
        ForwardSpeed -= ForwardSpeed * AirDrag * Mathf.Abs(ForwardSpeed) * delta;

        // Handbrake scrubs a little speed while sliding.
        if (DriftState == VehicleDriftState.Drifting)
        {
            ForwardSpeed -= ForwardSpeed * DriftSpeedScrub * delta;
        }

        ForwardSpeed = Mathf.Clamp(ForwardSpeed, -MaxReverseSpeed, topSpeed);
    }

    // ---------------------------------------------------------------------
    // Steering
    // ---------------------------------------------------------------------

    private void UpdateSteering(float delta)
    {
        float speed = Mathf.Abs(ForwardSpeed);

        // No authority at a standstill; less of it at top speed.
        float lowSpeedAuthority = Mathf.Clamp(speed / 4.0f, 0.0f, 1.0f);
        float highSpeedAuthority = Mathf.Lerp(1.0f, HighSpeedSteerFactor, Mathf.Clamp(speed / MaxSpeed, 0.0f, 1.0f));
        float authority = lowSpeedAuthority * highSpeedAuthority;

        if (!IsOnFloor())
        {
            authority *= 0.35f; // keep airborne attitude changes subtle
        }

        float yawRate = Mathf.DegToRad(BaseTurnRateDegrees) * authority;
        if (DriftState == VehicleDriftState.Drifting)
        {
            yawRate *= DriftYawMultiplier;
        }

        // Reversing flips the steering response, like a real car.
        float direction = ForwardSpeed < -0.1f ? -1.0f : 1.0f;

        // Godot: +Y is up and forward is -Z, so a right turn is a negative rotation.
        RotateY(-_smoothedSteer * yawRate * direction * delta);
    }

    // ---------------------------------------------------------------------
    // Velocity composition
    // ---------------------------------------------------------------------

    private void ComposeVelocity(float delta)
    {
        Basis basis = GlobalTransform.Basis;

        // Flatten the chassis axes so wheelspin/lean can never make the car "fly".
        var forward = new Vector3(basis.Z.X, 0.0f, basis.Z.Z).Normalized() * -1.0f;
        var right = new Vector3(basis.X.X, 0.0f, basis.X.Z).Normalized();

        float lateral = Velocity.Dot(right);

        // Grip = how quickly sideways speed is erased. Exponential decay is
        // frame-rate independent and cheaper than a per-frame clamp loop.
        float damping = DriftState == VehicleDriftState.Drifting
            ? DriftLateralDamping
            : (IsOnFloor() ? GripLateralDamping : 0.4f);
        lateral *= Mathf.Exp(-damping * delta);

        if (DriftState == VehicleDriftState.Drifting)
        {
            // Feed the slide outward: steering into the drift widens the arc.
            lateral += -_smoothedSteer * Mathf.Abs(ForwardSpeed) * DriftSlideFactor * delta;
        }

        float vertical = Velocity.Y;
        if (IsOnFloor())
        {
            vertical = -GroundStickForce; // pin to the surface over crests
        }
        else
        {
            vertical -= Gravity * delta;
        }

        Velocity = forward * ForwardSpeed + right * lateral + Vector3.Up * vertical;
    }

    // ---------------------------------------------------------------------
    // Drift charge / boost payout
    // ---------------------------------------------------------------------

    private void UpdateDriftCharge(float delta)
    {
        bool driftingNow = DriftState == VehicleDriftState.Drifting;

        if (driftingNow)
        {
            // Longer, harder-steered slides charge faster.
            float intensity = 0.35f + Mathf.Abs(_smoothedSteer);
            DriftCharge = Mathf.Min(1.0f, DriftCharge + DriftChargePerSecond * intensity * delta);
        }
        else
        {
            if (_wasDrifting && DriftCharge >= DriftChargeThreshold)
            {
                TriggerDriftBoost();
            }

            DriftCharge = Mathf.MoveToward(DriftCharge, 0.0f, DriftChargeDecayPerSecond * delta);
        }

        _wasDrifting = driftingNow;
    }

    /// <summary>Pays out the stored drift charge as an instant shove plus a short power window.</summary>
    private void TriggerDriftBoost()
    {
        DriftCharge = 0.0f;
        ForwardSpeed += DriftBoostImpulse;
        _boostTimer = DriftBoostDuration;
        SetDriftState(VehicleDriftState.DriftBoost);
    }

    // ---------------------------------------------------------------------
    // Impact response
    // ---------------------------------------------------------------------

    protected override void PostMove(float delta)
    {
        int collisions = GetSlideCollisionCount();
        if (collisions == 0)
        {
            return;
        }

        Vector3 forward = -GlobalTransform.Basis.Z;
        for (int i = 0; i < collisions; i++)
        {
            Vector3 normal = GetSlideCollision(i).GetNormal();

            // Head-on wall: kill most of the forward speed instead of grinding along it.
            if (normal.Dot(forward) < -0.55f)
            {
                ForwardSpeed *= 0.25f;
                if (DriftState == VehicleDriftState.Drifting)
                {
                    DriftCharge = 0.0f; // a crash should not pay out a boost
                }
                break;
            }
        }
    }
}
