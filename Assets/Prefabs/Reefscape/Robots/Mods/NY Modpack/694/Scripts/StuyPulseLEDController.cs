using System.Collections.Generic;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// 694-specific LED controller. Auto align (reef, barge, or station) takes over the strip first,
    /// mirroring how the real robot's alignment commands require the LED subsystem and pre-empt
    /// LEDDefaultCommand while they're running. Below that sits the real LEDDefaultCommand's own priority
    /// chain (github.com/StuyPulse/Aunt-Mary):
    /// scoring, climbed/climbing, climb open, algae intake, froggy coral intake, processor, has-coral, then
    /// a Coral/Algae mode idle color - the strip is never actually driven fully off, "off" just means dim
    /// (offIntensity) instead of 0. Reads only the public state already exposed by ReefscapeRobotBase and
    /// the game piece controllers, so it can be dropped onto the existing 694 rig without editing it. Colors
    /// default to the values used in the real robot's Settings.LED constants.
    ///
    /// One addition beyond the real robot's own LED chain: froggy/L1 coral mode gets its own visual states
    /// (froggyModeColor while in the mode generally, froggyCoralHeldColor once a piece is actually secured
    /// in froggy) slotted in right after algae intake and above the existing narrower froggyIntakeCoralColor
    /// state, so switching into froggy coral mode is visually distinct even outside that narrower moment.
    ///
    /// LED surfaces are wired the same way as GRRLights (340) and the framework's LEDStripController: assign
    /// the generated Shader from Assets/Materials/LEds/LEDs.shadergraph to shaderGraphShader and drag the LED
    /// mesh GameObject(s) into `leds`; this script builds one shared material instance and assigns it to each
    /// of their renderers at Start. That shader has no color input though - only _Texture2D/_X/_Y/_intensity -
    /// so instead of requiring a texture asset per state, this generates a small solid-color Texture2D at
    /// runtime from each Color field below and feeds that into _Texture2D. _intensity is what drives on/off/blink.
    /// </summary>
    public class StuyPulseLEDController : MonoBehaviour
    {
        [Header("LED Surfaces")]
        [Tooltip("Drag every GameObject (with a Renderer) that makes up the main LED strip here.")]
        [SerializeField] private GameObject[] leds;

        [Tooltip("Optional: the subset of the strip that lights up for the left side during a side-specific state (align left, etc). Falls back to leds if empty.")]
        [SerializeField] private GameObject[] leftAccentLeds;

        [Tooltip("Optional: the subset of the strip that lights up for the right side. Falls back to leds if empty.")]
        [SerializeField] private GameObject[] rightAccentLeds;

        [Tooltip("The generated Shader asset from Assets/Materials/LEds/LEDs.shadergraph - the same one GRRLights (340) and LEDStripController use. If left empty, this clones whatever material is already on each LED mesh instead.")]
        [SerializeField] private Shader shaderGraphShader;

        [Header("Intensity")]
        [Tooltip("How bright a fully \"on\" state is. The shader's emission is HDR, so this can go well past 1 without clipping to white.")]
        [SerializeField] private float onIntensity = 150f;
        [Tooltip("The low phase of a blink, and the floor for any state - never fully 0, so the strip is always at least dimly lit instead of going dark.")]
        [SerializeField] private float offIntensity = 30f;
        [SerializeField] private float blinkPeriod = 0.5f;

        [Header("Colors (defaults match Settings.LED from the real robot code)")]
        [SerializeField] private Color scoreColor = Color.green;
        [SerializeField] private Color climbOpenColor = Color.yellow;
        [SerializeField] private Color climbingColor = Color.green;
        [SerializeField] private Color intakeAlgaeColor = Color.green;
        [SerializeField] private Color froggyIntakeCoralColor = Color.red;
        [Tooltip("General indicator for being in froggy/L1 intake mode at all, not just the narrower 'actively grabbing coral' moment froggyIntakeCoralColor covers. Not part of the real robot's LED chain - added on request for a visual mode distinction.")]
        [SerializeField] private Color froggyModeColor = new Color(1f, 0.5f, 0f); // orange
        [Tooltip("Coral physically secured in the froggy mechanism (HasFroggyCoral), distinct from the plain hasCoralColor below which doesn't otherwise distinguish froggy-held vs. normal/shooter-held coral.")]
        [SerializeField] private Color froggyCoralHeldColor = Color.magenta;
        [SerializeField] private Color reefAlignLeftColor = Color.yellow;
        [SerializeField] private Color reefAlignRightColor = Color.red;
        [SerializeField] private Color bargeAlignColor = Color.yellow;
        [SerializeField] private Color processorColor = new Color(0.5f, 0f, 0.5f); // purple
        [SerializeField] private Color hasCoralColor = Color.blue;
        [SerializeField] private Color disabledColorBlue = Color.blue;
        [SerializeField] private Color disabledColorRed = Color.red;

        [Tooltip("Idle fallback while in Coral mode and nothing more specific applies - matches the old, unused LEDStripController's CoralMode texture idea so the strip is never just off.")]
        [SerializeField] private Color coralModeColor = Color.white;
        [Tooltip("Idle fallback while in Algae mode and nothing more specific applies.")]
        [SerializeField] private Color algaeModeColor = new Color(0f, 1f, 1f); // cyan

        private ReefscapeRobotBase _base;
        private StuyPulseAutoAlign _autoAlign;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData> _pieces;
        private IStuyPulseGamePieceStatus _gamePieceStatus;

        private Material _stripMaterial;
        private Material _leftMaterial;
        private Material _rightMaterial;

        private readonly Dictionary<Color, Texture2D> _solidTextures = new();

        private float _scoreFlashUntil;
        private bool _hadCoral;
        private bool _hadAlgae;

        private void Start()
        {
            _base = GetComponent<ReefscapeRobotBase>();
            _autoAlign = GetComponent<StuyPulseAutoAlign>();
            _pieces = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>();
            _gamePieceStatus = GetComponent<IStuyPulseGamePieceStatus>();

            _stripMaterial = BuildSharedMaterial(leds);
            _leftMaterial = leftAccentLeds is { Length: > 0 } ? BuildSharedMaterial(leftAccentLeds) : _stripMaterial;
            _rightMaterial = rightAccentLeds is { Length: > 0 } ? BuildSharedMaterial(rightAccentLeds) : _stripMaterial;
        }

        // Same as GRRLights/LEDStripController: one shared, runtime-instanced material assigned across every
        // renderer in the group, so setting a texture/intensity on it updates all of them at once.
        private Material BuildSharedMaterial(GameObject[] objects)
        {
            if (objects == null || objects.Length == 0) return null;

            Material shared = null;
            foreach (var obj in objects)
            {
                if (obj == null || !obj.TryGetComponent<Renderer>(out var renderer)) continue;

                shared ??= shaderGraphShader != null ? new Material(shaderGraphShader) : new Material(renderer.sharedMaterial);
                renderer.material = shared;
            }

            return shared;
        }

        // The shader only takes a texture, not a color, so this bakes a tiny solid-color texture the first
        // time a given color is used and reuses it after that.
        private Texture2D GetSolidTexture(Color color)
        {
            if (_solidTextures.TryGetValue(color, out var existing)) return existing;

            var texture = new Texture2D(4, 4) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[texture.width * texture.height];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();

            _solidTextures[color] = texture;
            return texture;
        }

        private void Update()
        {
            if (_base == null) return;

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            var algae = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Algae.ToString()) : null;

            var hasCoral = coral != null && coral.HasPiece();
            var hasAlgae = algae != null && algae.HasPiece();

            // A piece was carried and is now gone while we were actively placing - treat it as a score, same
            // edge-detection idiom used by GRRLights (340) for its "just scored" flash.
            if ((_hadCoral && !hasCoral || _hadAlgae && !hasAlgae) &&
                _base.CurrentSetpoint == ReefscapeSetpoints.Place)
            {
                _scoreFlashUntil = Time.time + 0.4f;
            }

            _hadCoral = hasCoral;
            _hadAlgae = hasAlgae;

            var blink = Time.time % blinkPeriod > blinkPeriod / 2f ? onIntensity : offIntensity;

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                SetAll(_base.Alliance == Alliance.Blue ? disabledColorBlue : disabledColorRed, onIntensity);
                return;
            }

            // Auto align owns the LEDs while it's actually driving the robot, same as the real LEDApplyPattern
            // command taking over from LEDDefaultCommand for the duration of an alignment command.
            if (_autoAlign != null && _autoAlign.ReefAlignActive() &&
                (_base.AutoAlignLeftAction.IsPressed() || _base.AutoAlignRightAction.IsPressed()))
            {
                var left = _autoAlign.ReefAlignLeft();
                // Blink while still driving toward the target, solid once actually there - lets the driver see
                // at a glance whether it's safe to place yet instead of a constant blink the whole approach.
                // Unlike the shared `blink` value (which floors at offIntensity so the strip is never fully
                // dark elsewhere), this blink drops all the way to black - a more attention-grabbing "not
                // aligned yet" signal specifically requested for coral align.
                var reefBlink = Time.time % blinkPeriod > blinkPeriod / 2f ? onIntensity : 0f;
                var reefIntensity = _autoAlign.ReefAlignAtTarget() ? onIntensity : reefBlink;
                // SetSides (below) writes to _leftMaterial/_rightMaterial - but 694.prefab has no
                // leftAccentLeds/rightAccentLeds configured, so both fall back to the same shared
                // _stripMaterial instance (see Start()). Two SetSides calls against the same material just
                // stomp each other, and only the second (right-side) write actually stuck - which pinned the
                // left-pole case to a constant offIntensity with no blink at all, i.e. blinking only ever
                // showed up when aligning right, reading as "flashes every other coral." Since there's only
                // one physical strip on this robot right now, use SetAll with whichever side's color is
                // active instead of trying to split a single strip into two independently-lit halves.
                SetAll(left ? reefAlignLeftColor : reefAlignRightColor, reefIntensity);
            }
            else if (_autoAlign != null && _autoAlign.BargeAlignActive())
            {
                SetAll(bargeAlignColor, blink);
            }
            else if (Time.time < _scoreFlashUntil)
            {
                SetAll(scoreColor, onIntensity);
            }
            else if (_base.CurrentSetpoint == ReefscapeSetpoints.Climbed)
            {
                SetAll(climbingColor, blink);
            }
            else if (_base.CurrentSetpoint == ReefscapeSetpoints.Climb)
            {
                SetAll(climbOpenColor, onIntensity);
            }
            else if (_base.IsIntaking && !hasAlgae &&
                     (_base.CurrentSetpoint is ReefscapeSetpoints.HighAlgae or ReefscapeSetpoints.LowAlgae ||
                      (_base.CurrentRobotMode == ReefscapeRobotMode.Algae &&
                       _base.CurrentSetpoint is ReefscapeSetpoints.Intake or ReefscapeSetpoints.Stack)))
            {
                // HighAlgae/LowAlgae are unambiguous algae setpoints, so they win regardless of
                // CurrentRobotMode (you can be in Coral mode while sitting at an algae setpoint). Intake and
                // Stack are shared with coral, so those still need CurrentRobotMode to disambiguate.
                SetAll(intakeAlgaeColor, onIntensity);
            }
            else if (_gamePieceStatus != null && _gamePieceStatus.HasFroggyCoral && _gamePieceStatus.HasShooterAlgae &&
                     (_base.CurrentSetpoint == ReefscapeSetpoints.Processor || _base.CurrentSetpoint == ReefscapeSetpoints.L1))
            {
                // Holding a froggy coral and a shooter algae at once only happens while resolving the stack
                // button (StuyPulseClean/StuyPulseNewArmClean.ResolveStackOrder picks Processor vs L1 based on
                // which piece was grabbed first) - froggy coral stays physically held for the whole Processor
                // sequence, so without this the froggyCoralHeldColor branch below would mask which outcome was
                // actually picked for the entire scoring cycle. algaeModeColor/coralModeColor reused here to
                // match the "which mode" idiom already used for the idle fallback further down.
                SetAll(_base.CurrentSetpoint == ReefscapeSetpoints.Processor ? algaeModeColor : coralModeColor, blink);
            }
            else if (_gamePieceStatus != null && _gamePieceStatus.HasFroggyCoral)
            {
                // Coral is physically secured in froggy - takes priority over the narrower "actively
                // grabbing" and general "in the mode" froggy states below, and over the plain hasCoral blue
                // further down, since none of those distinguish froggy-held from normal/shooter-held coral.
                SetAll(froggyCoralHeldColor, blink);
            }
            else if (_base.CurrentIntakeMode == ReefscapeIntakeMode.L1 && _base.CurrentSetpoint == ReefscapeSetpoints.Intake && !hasCoral)
            {
                SetAll(froggyIntakeCoralColor, onIntensity);
            }
            else if (_base.CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                // General froggy/L1 mode indicator for any other moment in this mode - e.g. just having
                // switched into it but not yet intaking - so entering froggy coral mode is visually distinct
                // even outside the two narrower states above.
                SetAll(froggyModeColor, onIntensity);
            }
            else if (_base.CurrentSetpoint == ReefscapeSetpoints.Processor)
            {
                SetAll(processorColor, onIntensity);
            }
            else if (hasCoral)
            {
                SetAll(hasCoralColor, blink);
            }
            else
            {
                // Never just "off" - fall back to a steady mode indicator instead of a blank strip. An
                // algae-intent setpoint (HighAlgae/LowAlgae) says algae regardless of CurrentRobotMode, same
                // reasoning as the intake-color check above; only Stow/etc fall back to CurrentRobotMode.
                var showAlgae = _base.CurrentRobotMode == ReefscapeRobotMode.Algae ||
                                 _base.CurrentSetpoint is ReefscapeSetpoints.HighAlgae or ReefscapeSetpoints.LowAlgae;
                SetAll(showAlgae ? algaeModeColor : coralModeColor, onIntensity);
            }
        }

        private void SetAll(Color color, float intensity)
        {
            var texture = GetSolidTexture(color);
            Set(_stripMaterial, texture, intensity);
            Set(_leftMaterial, texture, intensity);
            Set(_rightMaterial, texture, intensity);
        }

        private void SetSides(Color leftColor, Color rightColor, float leftIntensity, float rightIntensity)
        {
            Set(_leftMaterial, GetSolidTexture(leftColor), leftIntensity);
            Set(_rightMaterial, GetSolidTexture(rightColor), rightIntensity);
        }

        // Same idea as GRRLights' Set(): _X/_Y stay at 0 (they're the shader's scroll offset, unused here),
        // _intensity is what actually drives on/off/blink, and _Texture2D gets the baked solid-color texture.
        private void Set(Material material, Texture texture, float intensity)
        {
            if (material == null) return;

            material.SetFloat("_X", 0f);
            material.SetFloat("_Y", 0f);
            material.SetFloat("_intensity", intensity);
            material.SetTexture("_Texture2D", texture);
        }
    }
}
