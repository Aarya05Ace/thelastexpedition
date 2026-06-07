// RescueMission.cs. THE LAST EXPEDITION, Mission 1 ("Reach the cabin. Bring her home."). Self-bootstrapping,
// owns the whole scripted beat with NO scene wiring and NO edits to any other file.
//
// THE BEAT (hardcoded, demo-proof):
//   1) TrekToCabin. a CABIN is placed at the FAR END of the map (a large fixed distance off the player spawn
//      along spawn-forward, ground-snapped, door turned to face the approach) with the SISTER waiting INSIDE,
//      idle, facing the door (NpcModels female slot, Humanoid via CharacterRig.Apply, Neutral so the gauntlet
//      bodyguards never shoot her). The ExpeditionPopulation agent scatters a gauntlet of guards between the
//      spawn and the cabin (it reads RescueMission.CabinPosition). HUD: "Reach the cabin at the far end of the
//      island. Cut through the Curator's men."
//   2) Reunion. the player enters the cabin (a distance poll on CabinPosition). The sister SEES the player:
//      she plays the Jump animation (Animator trigger "Jump"), then RUNS to the player (a fast SisterFollow
//      mover that drives the Animator "Speed" so she sprints with real locomotion). On arrival a short, paced,
//      readable EMOTIONAL REUNION conversation plays: the sister speaks via the media-server /tts (female
//      voice); the player's lines show as "You: ..." subtitles. Cinematic (Barlow via StorySequencer, backing
//      panel + drop shadow, paced hold timing, skippable with Space/E).
//   3) Reveal -> BossFight -> Victory. about 4 to 5 seconds AFTER the reunion completes, the cinematic stage
//      stays held and the Curator is REVEALED in place: his BODY manifests (oversized, dark-tinted), the camera
//      frames ONLY him (the orbit rig is disabled for the beat), and a deep ElevenLabs Adam voice delivers a
//      roughly 7-second dark speech (monstrous, never graphic). Then the camera is restored, he is armed with a
//      weapon + aggressive CombatAI and flanked by elites (BossFight), and his death resolves the run (Victory).
//      The boss is 100 GREEN + 100 BLUE = 200 total. The Reveal beat runs inside the Confront phase, so the
//      RescuePhase enum + the MissionHud bindings are untouched. (The old Escape walk-back is collapsed: the
//      reveal happens at the cabin right after the hug, matching "about 4 to 5 seconds after the reunion".)
//
// SHARED API (the MissionHud agent reads these EXACT names): enum RescuePhase; static RescueMission.Phase;
// static RescueMission.Objective; static event RescueMission.OnPhaseChanged. PLUS static Vector3
// RescueMission.CabinPosition (the placed cabin world position; the ExpeditionPopulation agent scatters the
// gauntlet between the player spawn and this point). All live on this one class.
//
// WHY THE GUARDS HUNT THE PLAYER WITH NO NEW AI CODE: CombatAI.AcquireTarget() targets the nearest LIVE
// Health of a DIFFERENT faction within sightRange, skipping same-faction and Neutral. The boss + elites are
// Faction.Bodyguard; the player is Faction.Survivalist (PlayerCombat.PlayerFaction); the sister is
// Faction.Neutral, so she is never targeted.
//
// WHY THE SISTER JUMPS THEN SPRINTS WITH REAL LOCOMOTION: she is a raw model with CharacterRig.Apply only (NO
// NpcAgent), so she keeps the FULL Locomotion controller (it has the "Jump" trigger AND the "Speed" blend
// float). SetTrigger("Jump") fires the jump state; SisterFollow then walks her transform and CharacterLocomotion
// reads the per-frame delta -> SetFloat("Speed") -> the locomotion blend tree animates a real run.
//
// BUILD-SAFE: the cabin loads via Resources.Load<GameObject>("Environment/Cabin") (a copy of the asset-store
// Cabin.prefab placed in Resources WITHOUT its .meta, so its mesh/material resolve by their original GUIDs)
// with an editor-only AssetDatabase fallback (UnityEditor.* under #if UNITY_EDITOR only). Models/weapons/
// controllers all go through the existing Resources-primary resolvers. Pure UnityEngine, no SpacetimeDB types
// (-> no Vector3 alias needed), no System namespace (System.Action / System.Exception / System.Collections /
// System.Text are fully qualified, so bare Object resolves to UnityEngine.Object). One class per name.
// Null-safe throughout; never throws. No em dashes, no emoji in UI.

using UnityEngine;

// The scripted phases (separate top-level type, in the same file). Phase order = enum order, so the default
// value (0) is TrekToCabin and the HUD reads the right opening line before anything runs.
//
// CONTINUOUS MULTI-ACT STORY: TrekToCabin -> Reunion flow, with NO dead end, into the Escape -> Confront ->
// BossFight -> Victory finale (KEPT verbatim from the prior build).
public enum RescuePhase { TrekToCabin, Reunion, Escape, Confront, BossFight, Victory }

public class RescueMission : MonoBehaviour
{
    // ---- tunables (all hardcoded for the demo) -------------------------------------------------------

    const float PollInterval = 0.5f;     // seconds between readiness polls while waiting

    const float CabinDistance = 50f;     // metres along the walkable spawn heading. 50 keeps the cabin in the
                                         // OPEN flat area BEFORE the cliffs (so it stays reachable now that the
                                         // ProBuilder cliff/rock collision is back ON), not bunching at the
                                         // door. The amber beacon (AddBeacon) still marks it from spawn, and the
                                         // cabin foot is ground-snapped so it follows terrain automatically.
                                         // EDITOR CHECK: at 80m up +Z from the mission origin, confirm the cabin
                                         // foot still lands on walkable terrain inside the reachable box and the
                                         // spawn to cabin path is traversable; nudge MissionLocalOrigin /
                                         // MissionHeading if it lands in water, off-map, or a steep zone.
    const float CabinEnterRange = 4.5f;  // metres from CabinPosition that fires Reunion (player is "inside")

    // Reunion run-in: the sister sprints to the player, then closes for the dialogue.
    const float ReunionRunSpeed = 5.5f;     // m/s: drives "Speed" high -> the run end of the blend tree
    const float ReunionRunStop  = 1.6f;     // metres: she stops this close for the conversation (a real hug-range)
    const float JumpHold         = 1.1f;    // seconds the Jump beat plays before she starts running
    const float RunTimeout       = 6.0f;    // seconds: if she cannot reach the player, start the dialogue anyway

    // Curator finale tunables (KEPT) ------------------------------------------------------------------

    const float BossHealth    = 100f;   // GREEN pool. 100 green + 100 blue = 200 total (down from 500)
    const float BossShield    = 100f;   // BLUE pool. drained first, so the shield must be stripped before HP drops
    const float BossScale     = 1.35f;  // visibly larger silhouette than a grunt
    const float BossBlockDist = 10f;    // metres past the landing the boss spawns, blocking the way home
    const int   EliteCount    = 3;      // elite guards flanking Ezra
    const float EliteRing     = 6f;     // metres from Ezra the elites spawn
    const float EliteHealth   = 150f;   // a step up from the 100-HP grunts

    // THE REVEAL beat (between the reunion finishing and the fight). About 4 to 5 seconds after the hug the
    // Curator is REVEALED (we see only him), a deep evil speech plays for roughly 7 seconds, THEN the fight.
    const float RevealDelay   = 4.5f;   // seconds after the reunion completes before the Curator is revealed
    const float RevealSpeech  = 7.5f;   // seconds the dark speech holds on the framed Curator before the fight

    // REVEAL FRAMING (fit-to-bounds, so we see his WHOLE model, feet included). The camera distance is derived
    // from his measured renderer height so any model size frames cleanly; the look target is his bounds CENTER
    // (not his head), so his legs never fall out of the bottom of the frame.
    const float RevealFitMargin  = 1.30f;  // headroom + footroom: 30 percent padding around the measured height
    const float RevealYaw        = 35f;    // degrees of 3/4 yaw off straight-on (true 3/4 hero read, scales with distance)
    const float RevealFov        = 55f;    // explicit vertical FOV during the reveal (tighter hero portrait); restored after
    const float RevealMinStandoff = 2.5f;  // floor on the camera distance so a short model still reads
    const float RevealHighBias   = 0.10f;  // lift the cam above bounds-center by this fraction of the height (flattering slight-high 3/4)

    // ROCK CLEARANCE radii (the ProBuilder rock colliders are removed; clearance is renderer-bounds based via
    // RockClearance.ClearOfRocks, the static the terrain agent provides). Generous on the boss so the whole
    // reveal frame is clear of intersecting rock geometry, not just his capsule.
    const float BossClearRadius  = 3.0f;   // boss footprint + reveal-frame margin (his body radius is ~0.7 m scaled)
    const float EliteClearRadius = 1.5f;   // each flanking elite
    const float CabinClearRadius = 5.0f;   // cabin footprint: a building, so a wide margin so the whole shell (and
                                           // the sister placed inside it) lands clear of any visible boulder

    // The Curator's voice. ElevenLabs "Adam" (deep, menacing) so the boss is audibly distinct from the
    // grunts. The /tts sidecar already honors an optional voiceId and falls back to its DEFAULT_VOICE.
    const string CuratorVoiceId = "pNInz6obpgDQGcFmaJgB";

    // The rescued sister's voice. ElevenLabs "Rachel" (a warm, bright, youthful FEMALE voice), passed
    // explicitly as voiceId so she sounds HAPPY + EXCITED and audibly distinct from the wary chat NPCs (who
    // resolve their voice from gender). The /tts sidecar honors an optional voiceId over the gender field and
    // falls back to its DEFAULT_VOICE if it is empty, so this stays best-effort + null-safe. We ALSO pass
    // gender:"female" alongside it so a sidecar that ignores voiceId still picks a female voice.
    // EDITOR/AUDIO CHECK: confirm this voice reads as happy/excited on the chosen ElevenLabs account; swap the
    // id if a brighter female voice is preferred (subjective "feel" check, per the project's eyeball rule).
    const string SisterVoiceId = "21m00Tcm4TlvDq8ikWAM";

    // DETERMINISTIC MISSION FRAME. The cabin, sister, landing and Curator all derive from this FIXED world
    // frame (NOT the local player's transform), so every client builds them at the SAME world position and
    // all players see the mission entities consistently. The frame is anchored to NetworkedWorld.WorldOffset
    // (a shared static, identical on every client) plus a fixed local offset, then ground-snapped, so a fixed
    // (x,z) plus a fixed heading is deterministic across clients. (Out of scope: true shared boss HP and
    // absolute cross-client wall-clock timing of the reveal, which both need server-side state. This frame
    // guarantees identical GEOMETRY for sister/cabin/Curator/elites and identical beat DURATIONS, which is the
    // in-scope determinism.)
    //
    // IN-EDITOR CHECK: MissionLocalOrigin + MissionHeading must land on walkable terrain with the cabin
    // reachable along the heading and the spawn->cabin gauntlet traversable. The offsets below place the
    // mission origin AT the forest origin (worldOffset) facing world +Z, matching the area the single-player
    // spawn already used. Verify in the editor that the cabin (CabinDistance metres up +Z) sits on walkable
    // ground; nudge MissionLocalOrigin / MissionHeading if it lands in a steep or restricted zone.
    static readonly Vector3 MissionLocalOrigin = new Vector3(0f, 0f, 0f); // offset from WorldOffset, in world units
    static readonly Vector3 MissionHeading     = Vector3.forward;          // fixed flat heading toward the far end

    // ---- SHARED STATIC API (the MissionHud + ExpeditionPopulation agents bind to these EXACT names) ---

    // Current phase. Defaults to TrekToCabin (enum 0) so the HUD reads correctly before setup runs.
    public static RescuePhase Phase { get; private set; }

    // The hardcoded objective line for the current phase (verbatim copy; no em dashes, no emoji).
    public static string Objective => Phase switch
    {
        RescuePhase.TrekToCabin => "Reach the cabin at the far end of the island. Cut through the Curator's men.",
        RescuePhase.Reunion     => "She is here. Go to her.",
        RescuePhase.Escape      => "Get her to the shore. Do not stop.",
        RescuePhase.Confront    => "The Curator is waiting. He will not let you leave.",
        RescuePhase.BossFight   => "End it. Put the Curator down.",
        RescuePhase.Victory     => "You got her out. You made it home.",
        _ => "",
    };

    // The placed cabin world position (= the sister + the trek goal). The ExpeditionPopulation agent reads
    // this to scatter the gauntlet between the player spawn (landingPos) and here. Set deterministically in
    // SetupMission BEFORE the cabin/guards need it, so every client agrees on the far end.
    public static Vector3 CabinPosition { get; private set; }

    // The landing / extraction point (= the player spawn = the shore they came from). The map agent reads this
    // to pin the EXTRACTION objective during Escape / Confront. Set in SetupMission alongside landingPos. Read-only.
    public static Vector3 LandingPosition { get; private set; }

    // The Curator's spawned world position (Ezra). The map agent reads this to pin the boss during BossFight.
    // Vector3.zero until SpawnCuratorBody runs (the map gates the boss pin on Phase == BossFight, so zero is never shown).
    public static Vector3 BossPosition { get; private set; }

    // Fires exactly once per real phase change (the HUD re-reads Objective + fade-punches on this).
    public static event System.Action OnPhaseChanged;

    // Set the phase; fire OnPhaseChanged once per actual change. Swallows subscriber exceptions so one bad
    // HUD never blocks the mission. Static so internal transitions and the (private) setter share one path.
    static void SetPhase(RescuePhase next)
    {
        if (Phase == next) return;
        Phase = next;
        try { OnPhaseChanged?.Invoke(); }
        catch (System.Exception e) { Debug.LogException(e); }
    }

    // ---- instance state -----------------------------------------------------------------------------

    bool setup;            // SetupMission has run (cabin + sister placed)
    bool cabinEntered;     // player has reached the cabin (one-shot; locks the TrekToCabin proximity poll)
    bool reunionDone;      // the reunion conversation finished -> the Escape->Confront->Boss chain is live
    float pollTimer;

    Transform player;      // cached LocalPlayer transform for the whole mission
    GameObject cabin;      // the placed cabin shell
    GameObject sister;     // the waiting / freed sister
    Animator sisterAnim;   // the sister's Animator (Jump trigger + Speed float)

    // Reunion sequencing state.
    bool reunionStarted;        // BeginReunion has run (one-shot)
    int reunionStep;            // -1 = jump+run-in pre-roll; 0..N = the scripted lines
    float reunionStepAt;        // Time.time at/after which the current beat advances
    float reunionRunDeadline;   // Time.time by which the run-in gives up and the dialogue starts anyway
    SisterFollow reunionMover;  // the fast run-in mover (re-tuned to a slow follow when the talk ends)
    string subtitleText;        // the line OnGUI currently draws (null/empty = nothing)
    bool subtitleIsSister;      // true -> SemiBold weight (sister); false -> Regular weight ("You:")
    AudioSource sisterVoice;    // 2D source the sister's TTS lines play on (lazy-created)
    NpcAgent sisterAgent;       // added AFTER the reunion so the [V]/[E] prompt sees her (one-shot; null = not yet)

    // ---- Curator finale state (KEPT) ----------------------------------------------------------------

    Vector3 landingPos;    // the deterministic landing / shore (= the mission frame origin), NOT the local spawn
    Vector3 missionHeading; // the deterministic flat heading toward the far end (cabin / boss placement)
    bool bossSpawned;      // Ezra + elites spawned once (ArmCurator has run -> the fight is live)
    Health bossHealth;     // Ezra's Health; OnDied -> Victory
    GameObject boss;       // Ezra "The Curator"
    AudioSource curatorVoice; // 2D source the Curator's TTS lines play on (lazy-created)

    // ---- THE REVEAL beat state (fires after the reunion, before the fight) -----------------------------
    int   revealStep;        // 0 = waiting out RevealDelay; 1 = framed + speaking; 2 = done (fight armed)
    float revealStepAt;      // Time.time at/after which the current reveal step advances
    bool  bossBodyStanding;  // the Curator BODY has been spawned (for the reveal) but has NO brain/gun yet
    ExpeditionCamera revealCam; // the orbit controller we disable during the reveal, re-enable after (null-safe)
    Vector3 revealFocus;     // the world point on the Curator the reveal camera frames ("we see him only")
    Transform revealCamTf;   // the camera transform we drive directly while the controller is disabled

    // Bounds-fit framing cache. Measured ONCE from the Curator's renderers after he is spawned + scaled (he is
    // inert during the reveal, so a one-time cache is correct). bossHeight = his full world height, bossCenterY
    // = the world Y of his bounds center (feet + height/2). Fall back to BossScale-derived sizes if no renderers.
    float bossHeight;        // measured world height of the Curator model (renderer bounds)
    float bossCenterY;       // measured world Y of the Curator's bounds center (the reveal look target height)
    Camera revealCamObj;     // the live Camera we set RevealFov on during the reveal (restored in EndRevealCamera)
    float savedFov;          // the Camera's FOV before the reveal, restored when the reveal ends

    // cached Barlow fonts (lazy; fall back to the StorySequencer fallback if Barlow is missing)
    Font sisterFont;       // SemiBold, for the sister's spoken lines
    Font youFont;          // Regular, for the "You:" subtitle lines

    // ---- the scripted reunion conversation (no em dashes, no emoji; sister = TTS, you = subtitle) -----
    //
    // Each beat: speaker (true = sister/spoken, false = you/subtitle-only), the line, and how long it holds
    // before auto-advancing (read + hear time). The whole beat is skippable (Space/E jumps to the next line).
    struct ReunionLine { public bool sister; public string text; public float hold; }

    static readonly ReunionLine[] Reunion =
    {
        new ReunionLine { sister = true,  text = "It is you. It is really you. You found me.",                hold = 4.0f },
        new ReunionLine { sister = true,  text = "I knew it. I knew you would never leave me here.",          hold = 4.0f },
        new ReunionLine { sister = false, text = "I would tear this whole island apart for you. You know that.", hold = 4.0f },
        new ReunionLine { sister = true,  text = "Look at you. You came all this way. For me.",               hold = 4.0f },
        new ReunionLine { sister = false, text = "Always. Now stay close and we walk out of here together.",  hold = 4.0f },
        new ReunionLine { sister = true,  text = "Together. Yes. Let's go home. I am so happy. Let's go home.", hold = 4.0f },
    };

    // ---- self-bootstrap (no scene wiring; mirrors ExpeditionPopulation / CombatSpawner / AudioDirector) -

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        var go = new GameObject("RescueMission");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<RescueMission>();
    }

    // ---- readiness gate + per-frame drive -----------------------------------------------------------

    void Update()
    {
        // Phase 0: wait for gameplay + a local player, then run SetupMission ONCE. We keep running afterward
        // for the trek proximity poll, the reunion sequencer, and the Escape/Confront/Boss chain.
        if (!setup)
        {
            pollTimer += Time.deltaTime;
            if (pollTimer < PollInterval) return;
            pollTimer = 0f;

            if (!NetworkedWorld.GameplayActive) return;

            var lp = FindFirstObjectByType<LocalPlayer>();
            if (lp == null) return;

            player = lp.transform;
            SetupMission();
            setup = true;
            return;
        }

        if (player == null) return;

        // ----- TrekToCabin: poll the distance to the cabin; entering it starts the reunion. -----
        if (Phase == RescuePhase.TrekToCabin)
        {
            if (!cabinEntered && FlatDistance(player.position, CabinPosition) <= CabinEnterRange)
            {
                cabinEntered = true;
                SetPhase(RescuePhase.Reunion);
                BeginReunion();
            }
            return;
        }

        // ----- Reunion: drive the jump + run-in, then the paced, skippable conversation. -----
        if (Phase == RescuePhase.Reunion)
        {
            DriveReunion();
            return;
        }

        // ----- The Reveal -> BossFight -> Victory chain. Live once the reunion is done. -----
        if (!reunionDone) return;

        // Act V/VI: the REVEAL beat runs in the Confront phase (armed by FinishReunion). It waits out the delay,
        // spawns the Curator BODY and frames the camera on him while his deep speech plays, then arms the fight.
        if (Phase == RescuePhase.Confront)
        {
            DriveReveal();
            return;
        }

        // Act VII: BossFight runs entirely on the existing CombatAI (Ezra + elites hunt the player). Ezra's
        // death -> Victory is handled by HandleBossDied (Health.OnDied), so nothing to poll.
    }

    // =================================================================================================
    // THE REVEAL: about 4 to 5 seconds after the reunion, the Curator is REVEALED. Step 0 waits out the delay;
    // step 1 spawns his BODY (no brain, no gun), frames the camera on him so we see ONLY him, and plays his
    // ~7-second dark speech; step 2 restores the camera, arms his AI + weapon + elites, and breaks into the
    // fight. Each step is driven off Time.time so the beat durations are identical on every client.
    // =================================================================================================

    void DriveReveal()
    {
        // While the reveal camera is held, keep it pinned on the Curator every frame (the orbit controller is
        // disabled, so nothing else moves the camera; this re-frames in case the boss settles on the ground).
        if (revealStep == 1) HoldRevealCamera();

        if (Time.time < revealStepAt) return;

        if (revealStep == 0)
        {
            BeginReveal();                              // spawn the body, frame the camera, start the speech
            revealStep = 1;
            revealStepAt = Time.time + RevealSpeech;    // hold the framed shot for the whole speech (~7s)
            return;
        }

        if (revealStep == 1)
        {
            EndRevealAndFight();                         // restore camera, arm the boss + elites, begin the fight
            revealStep = 2;
            return;
        }
    }

    // Spawn the Curator BODY (standing, inert) so the camera can frame him, point the camera at him, and play
    // his deep evil speech. He gets NO CombatAI and NO weapon yet (ArmCurator wires those when the fight starts),
    // so during this beat "we see him only": no elites, no shooting, just the collector revealing himself.
    void BeginReveal()
    {
        SpawnCuratorBody();                              // deterministic placement + Health (100 green + 100 blue)
        PlayAlarm();                                     // a dread sting as he is revealed
        BeginRevealCamera();                             // disable the orbit controller, frame the Curator

        // The ~7-second dark speech via ElevenLabs Adam (CuratorVoiceId). Monstrous, never graphic: he names
        // the trade as buying and selling forgotten people, depicting nothing. No em dashes, no emoji.
        SpeakCurator("You found her. How touching. I take what the world forgets and I sell it. " +
                     "She was inventory. Now so are you.");
    }

    // The reveal is over: restore the orbit camera, release the cinematic stage, then arm the Curator (brain +
    // gun) and spawn his elites so the fight begins. Reaching Victory is still driven by his Health.OnDied.
    void EndRevealAndFight()
    {
        EndRevealCamera();                              // re-enable the orbit controller, hand control back
        StorySequencer.ReleaseStage(this);             // drop the cinematic gate the reunion claimed
        SetPhase(RescuePhase.BossFight);               // "End it. Put the Curator down."
        ArmCurator();                                  // weapon + aggressive CombatAI + elites + the fight taunt
    }

    // ---- reveal camera: a scripted cutaway that frames ONLY the Curator ----
    //
    // ExpeditionCamera is a LateUpdate orbit rig with NO focus API, so any LookAt we do would be stomped the
    // next frame. The clean cutaway is to DISABLE the controller for the reveal window, drive the camera
    // transform directly, then RE-ENABLE it (which snaps it smoothly back behind the player). Null-safe: if the
    // camera or controller is missing the speech + on-screen beat still play, only the framing is skipped.

    void BeginRevealCamera()
    {
        if (boss == null) return;
        var cam = LocalPlayer.ActiveCamera != null ? LocalPlayer.ActiveCamera : Camera.main;
        if (cam == null) return;

        revealCamTf = cam.transform;
        revealCamObj = cam;
        revealCam = cam.GetComponent<ExpeditionCamera>();
        if (revealCam != null) revealCam.enabled = false;   // stop the orbit rig from stomping our framing

        // Set a deterministic reveal FOV so the fit-to-bounds distance is independent of sprint/ADS FOV state at
        // the moment the reveal fires. Restored in EndRevealCamera.
        savedFov = cam.fieldOfView;
        cam.fieldOfView = RevealFov;

        // Measure his renderer bounds ONCE (he is inert during the reveal, so the cache stays valid). bossHeight
        // = full world height, bossCenterY = world Y of the bounds center (feet + height/2). This is the same
        // combined-renderer-bounds pattern CharacterRig.Apply uses, but kept in WORLD space (we want world height
        // for the FOV fit). Null-safe: fall back to a BossScale-derived height if there are no renderers.
        MeasureBossBounds();

        HoldRevealCamera();
    }

    // Combine every renderer on the Curator into a world-space Bounds and cache his height + center Y for the
    // fit-to-bounds framing. Called once after the body is spawned + scaled. Null-safe with a sane fallback.
    void MeasureBossBounds()
    {
        bossHeight  = 2f * BossScale;                                  // fallback: a ~2 m model at BossScale
        bossCenterY = boss.transform.position.y + bossHeight * 0.5f;   // fallback center = feet + height/2

        var rends = boss.GetComponentsInChildren<Renderer>(true);
        if (rends == null || rends.Length == 0) return;

        bool have = false;
        Bounds world = new Bounds(boss.transform.position, Vector3.zero);
        for (int i = 0; i < rends.Length; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            if (!have) { world = r.bounds; have = true; }
            else world.Encapsulate(r.bounds);
        }
        if (!have) return;

        bossHeight  = Mathf.Max(world.size.y, 0.5f);   // guard a degenerate (flat) bound
        bossCenterY = world.center.y;
    }

    // Place + aim the cutaway camera to frame the Curator's WHOLE model: look at his bounds CENTER (so his feet
    // stay in frame), stand off far enough to fit his full measured height in the FOV (plus margin), at a clean
    // 3/4 yaw and roughly chest height (a flattering slight-high angle), and never below the terrain. Recomputes
    // each frame off the cached bounds so it re-fits if the body settles. Null-safe.
    void HoldRevealCamera()
    {
        if (revealCamTf == null || boss == null) return;

        // Look target = the bounds CENTER (his torso), NOT his head: a head-high target tilts the camera up and
        // drops his legs out of the bottom of the frame. Centering on the torso keeps the full model in shot.
        revealFocus = new Vector3(boss.transform.position.x, bossCenterY, boss.transform.position.z);

        // The deterministic facing: boss.forward points at the player (SpawnCuratorBody aims him there). Build a
        // clean 3/4 by rotating that flat forward RevealYaw degrees around up, so the offset stays a true 3/4 at
        // any fit distance (rather than a fixed perpendicular nudge that flattens as the camera pulls back).
        Vector3 fwd = boss.transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = -missionHeading;
        fwd.Normalize();
        Vector3 dir3q = Quaternion.AngleAxis(RevealYaw, Vector3.up) * fwd;   // unit, flat, yawed for the 3/4 read

        // Fit-to-bounds standoff: pull back so the full HEIGHT (x margin) fits the camera's vertical FOV.
        //   distance = (height/2 * margin) / tan(fov/2)
        // Use the live Camera FOV (we forced RevealFov in BeginRevealCamera, so this is deterministic). Guard a
        // missing camera / tiny FOV, and floor the standoff so a short model still reads.
        float fov = revealCamObj != null ? revealCamObj.fieldOfView : RevealFov;
        float halfFovRad = 0.5f * Mathf.Max(fov, 1f) * Mathf.Deg2Rad;
        float fitDist = (bossHeight * 0.5f * RevealFitMargin) / Mathf.Tan(halfFovRad);
        fitDist = Mathf.Max(fitDist, RevealMinStandoff);

        // Camera sits at bounds-center height (chest), lifted a touch above center for a flattering slight-high
        // 3/4 that still contains the feet, then pulled back along the yawed 3/4 direction.
        Vector3 camPos = revealFocus + dir3q * fitDist + Vector3.up * (bossHeight * RevealHighBias);

        // Terrain floor: never let the camera dip to/below the ground (that fills the bottom of the shot with the
        // terrain quad). GroundSnap finds the terrain Y under camPos; keep the camera a little above it.
        float groundY = GroundSnap(camPos).y + 0.3f;
        if (camPos.y < groundY) camPos.y = groundY;

        revealCamTf.position = camPos;
        revealCamTf.rotation = Quaternion.LookRotation((revealFocus - camPos).normalized, Vector3.up);
    }

    void EndRevealCamera()
    {
        if (revealCamObj != null) revealCamObj.fieldOfView = savedFov;   // restore the pre-reveal FOV
        if (revealCam != null) revealCam.enabled = true;    // hand control back to the orbit rig (it eases back)
        revealCam = null;
        revealCamTf = null;
        revealCamObj = null;
    }

    // ON-SCREEN WAYPOINT + DIAGNOSTIC so the cabin can ALWAYS be found, and so we can see if it actually
    // spawned. During the trek it shows the live distance + direction to the cabin; a small yellow line shows
    // whether the cabin object exists + its world position. Reaching within CabinEnterRange fires the reunion
    // even if the model is occluded, so the mission can never hard-stall on "I cannot find it".
    // Called from the single OnGUI (below) so this class defines OnGUI exactly once.
    void DrawCabinWaypoint()
    {
        if (!setup || player == null) return;
        float dist = Vector3.Distance(player.position, CabinPosition);

        if (Phase == RescuePhase.TrekToCabin)
        {
            var cam = LocalPlayer.ActiveCamera != null ? LocalPlayer.ActiveCamera : Camera.main;
            string dir = "AHEAD";
            Vector3 fwd = cam != null ? cam.transform.forward : player.forward; fwd.y = 0f;
            Vector3 to = CabinPosition - player.position; to.y = 0f;
            if (fwd.sqrMagnitude > 1e-3f && to.sqrMagnitude > 1f)
            {
                float ang = Vector3.SignedAngle(fwd.normalized, to.normalized, Vector3.up);
                if (Mathf.Abs(ang) < 30f) dir = "AHEAD";
                else if (Mathf.Abs(ang) > 150f) dir = "BEHIND YOU (turn around)";
                else dir = ang < 0f ? "to your LEFT" : "to your RIGHT";
            }
            // Lower-third placement (yFrac 0.62) keeps the waypoint clear of the top-center MissionHud
            // objective banner (spans roughly y40..y240 at 1080p) and above the bottom-center subtitle band
            // (subtitle at Screen.height - 140), so the trek waypoint never overlaps another HUD element.
            WaypointLabel($"CABIN   {dist:F0} m   {dir}", new Color(1f, 0.6f, 0.1f), 26, 0.62f);
        }
    }

    static void WaypointLabel(string text, Color col, int size, float yFrac)
    {
        var style = new GUIStyle(GUI.skin.label)
        { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = false };
        var rect = new Rect(0f, Screen.height * yFrac, Screen.width, size + 12f);
        style.normal.textColor = Color.black;
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, style);  // shadow
        style.normal.textColor = col;
        GUI.Label(rect, text, style);
    }

    // ---- setup: place the cabin at the far end + the waiting sister ----------------------------------

    void SetupMission()
    {
        // DETERMINISTIC FRAME (NOT the local player's transform): every client agrees on the landing, heading,
        // cabin, sister and Curator because they all derive from the shared NetworkedWorld.WorldOffset plus the
        // fixed MissionLocalOrigin / MissionHeading. (Using player.position / player.forward here was the source
        // of cross-client drift, since each client's player spawns at a different slot facing a different way.)
        Vector3 originXZ = NetworkedWorld.WorldOffset + MissionLocalOrigin;
        Vector3 spawn = GroundSnap(originXZ);

        // "Where they landed" = the deterministic shore (the mission frame origin). Captured once for the
        // Escape->Confront landing trigger and the boss placement. The codebase has no map-bounds API, so the
        // fixed frame origin is the deterministic, demo-proof "home" point.
        landingPos = spawn;
        LandingPosition = spawn;   // read-only mirror for the map agent (extraction = the shore)

        // "Far end of the island" = the FIXED mission heading + a LARGE distance off the origin, ground-snapped.
        // Deterministic on every client (no player.forward, no map-bounds lookup). Fall back to world +Z if the
        // configured heading is degenerate.
        Vector3 heading = MissionHeading; heading.y = 0f;
        if (heading.sqrMagnitude < 1e-4f) heading = Vector3.forward;
        heading.Normalize();
        missionHeading = heading;   // cached for the deterministic boss placement in SpawnCuratorBody

        // Place the cabin a fixed distance along the deterministic heading, ground-snapped. AddBeacon() raises a
        // tall glowing pillar so the cabin is visible from across the map and the player walks straight to it.
        Vector3 cabinXZ = spawn + heading * CabinDistance;
        // The ProBuilder rock COLLIDERS are removed, so a blind ground-snap could drop the cabin (and the sister
        // placed inside it) half inside a visible boulder. Route the XZ through RockClearance.ClearOfRocks FIRST
        // (renderer-bounds, the only way to detect rocks now that the colliders are gone), THEN ground-snap so the
        // final Y still sits on terrain. Same clear-horizontal-then-snap-vertical order the Curator placement uses.
        // ClearOfRocks is scene-deterministic (a fixed ring search over the same scene rock renderers), so every
        // client still agrees on the cabin position. The sister + door facing derive from cabinFoot, so they follow.
        Vector3 cabinFoot = GroundSnap(RockClearance.ClearOfRocks(cabinXZ, CabinClearRadius));

        // Publish the SHARED API BEFORE building anything, so the ExpeditionPopulation gauntlet reads a valid,
        // deterministic far-end the instant the mission is live.
        CabinPosition = cabinFoot;

        // Orient the cabin so its DOOR faces the approach (toward the player / spawn). The Cabin asset's local
        // +X axis points OUT through the door, so we rotate so local +X aligns with the toSpawn direction:
        // LookRotation aims local +Z, so compose a -90 deg yaw to swing local +X onto the look direction.
        Vector3 toSpawn = spawn - cabinFoot; toSpawn.y = 0f;
        if (toSpawn.sqrMagnitude < 1e-4f) toSpawn = -heading;
        toSpawn.Normalize();
        Quaternion cabinRot = Quaternion.LookRotation(toSpawn, Vector3.up) * Quaternion.Euler(0f, -90f, 0f);

        BuildCabin(cabinFoot, cabinRot);
        SpawnWaitingSister(cabinFoot, cabinRot, toSpawn);

        // Phase already defaults to TrekToCabin; this re-affirms it (and fires OnPhaseChanged if a reload left
        // a stale value), so the HUD shows the opening objective the instant the mission is live.
        SetPhase(RescuePhase.TrekToCabin);

        Debug.Log($"[RescueMission] Mission 1 ready. Cabin at {cabinFoot} ({CabinDistance} m off spawn {spawn}). " +
                  $"Sister {(sister != null ? "placed inside" : "MISSING")} (Neutral). Phase=TrekToCabin.");
    }

    // Place the cabin shell (the asset-store Cabin.prefab, copied into Resources/Environment WITHOUT its .meta
    // so its mesh + material resolve by their original GUIDs). Resources-primary with an editor-only
    // AssetDatabase fallback (mirrors NpcModels / CharacterRig). Build-safe, null-safe.
    void BuildCabin(Vector3 footPos, Quaternion rot)
    {
        GameObject prefab = Resources.Load<GameObject>("Environment/Cabin");
#if UNITY_EDITOR
        if (prefab == null)
            prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Cabin/Prefabs/Cabin.prefab");
#endif
        if (prefab == null)
        {
            Debug.LogWarning("[RescueMission] Cabin prefab not found in Resources/Environment/Cabin " +
                             "(or at Assets/Cabin/Prefabs/Cabin.prefab); raising the beacon at the goal anyway.");
            AddBeacon(footPos);   // still mark the objective so the player can navigate there
            return;
        }

        cabin = Object.Instantiate(prefab, footPos, rot);
        if (cabin == null) return;
        cabin.name = "RescueCabin";
        NpcModels.FixHdrp(cabin);   // the cabin ships Built-in materials that render MAGENTA in HDRP; re-shade
                                    // to HDRP/Lit (migrating the wood albedo) so it reads as wood, not purple.
        // The cabin pivot is at floor level, so place at the ground-snapped foot directly (no half-height lift,
        // unlike the old crate cube). Its MeshCollider stands in as world geometry; the door is an open gap.
        AddBeacon(footPos);
    }

    // The beacon's alpha. Low so the pillar reads as a TRANSLUCENT marker (a column of light), NOT a solid wall
    // you cannot see past. 0.35 keeps it clearly visible from across the map while you can still see the forest
    // (and the cabin) THROUGH it. The emissive glow does the heavy lifting for visibility, not the opacity.
    const float BeaconAlpha = 0.35f;

    // A tall glowing AMBER pillar above the cabin so the player can SEE the objective from across the map and
    // walk straight to it (the fix for "I cannot find the cabin"). A thin ~120m TRANSLUCENT emissive cylinder with
    // its collider removed so it never blocks movement and you can see straight through it. HDRP + builtin
    // emission set defensively, and the material is flipped to a TRANSPARENT surface at BeaconAlpha. Null-safe.
    void AddBeacon(Vector3 footPos)
    {
        // Bright, saturated amber so it reads by COLOR + glow against the green forest, at a LOW alpha so it is a
        // marker (a shaft of light), not an opaque tower. Wide and 120m tall so it is visible from spawn.
        var amber = new Color(1f, 0.55f, 0.05f, BeaconAlpha);
        var beacon = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        beacon.name = "CabinBeacon";
        var col = beacon.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);
        beacon.transform.position = footPos + Vector3.up * 60f;       // center 60m up -> spans ground to ~120m
        beacon.transform.localScale = new Vector3(3f, 60f, 3f);       // wide + very tall: unmissable from spawn
        var mr = beacon.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = mr.material;

            // Flip the material to a TRANSPARENT surface so the low alpha actually renders translucent (an opaque
            // material ignores alpha). HDRP/Lit reads _SurfaceType = 1 (Transparent) + the Transparent blend/render
            // queue; the legacy builtin keys are set too as a defensive fallback. Set BEFORE the colors so the
            // shader compiles in transparent mode. All guarded by HasProperty -> safe across HDRP/Lit and others.
            if (mat.HasProperty("_SurfaceType")) mat.SetFloat("_SurfaceType", 1f);   // HDRP/Lit: 0 opaque, 1 transparent
            if (mat.HasProperty("_BlendMode"))   mat.SetFloat("_BlendMode", 0f);     // HDRP/Lit: alpha blend
            if (mat.HasProperty("_DstBlend"))    mat.SetFloat("_DstBlend", 10f);     // OneMinusSrcAlpha
            if (mat.HasProperty("_SrcBlend"))    mat.SetFloat("_SrcBlend", 1f);      // One (premultiplied-style)
            if (mat.HasProperty("_ZWrite"))      mat.SetFloat("_ZWrite", 0f);        // do not write depth (see through)
            if (mat.HasProperty("_AlphaCutoffEnable")) mat.SetFloat("_AlphaCutoffEnable", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_BLENDMODE_ALPHA");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;    // 3000: draw after opaque

            // Amber base color carrying the low alpha (the translucency). Both HDRP + builtin color slots set.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", amber);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", amber);

            // Emission keeps it glowing/visible at the low alpha (the glow, not the opacity, sells the marker).
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", amber * 12f);  // HDRP Lit
            if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", amber * 12f);  // Builtin
        }
    }

    // Spawn the sister INSIDE the cabin, idle + Neutral, facing the door. Exact pipeline as the prior trapped
    // sister: model -> HDRP fix -> CharacterRig.Apply(go,false) -> Health(100, Neutral). No CombatAI, no
    // weapon, no wander. She stands inert (Speed ~0 -> idle clip) until the player enters and the reunion runs.
    // She keeps the FULL Locomotion controller (CharacterRig wires it, NOT NpcAgent's NpcIdle swap), so the
    // "Jump" trigger and the "Speed" run blend are both available for the reunion.
    void SpawnWaitingSister(Vector3 cabinFoot, Quaternion cabinRot, Vector3 toSpawn)
    {
        // BUILD-SAFE: "mara" -> NpcModels slot 0 (female v1), HUMANOID in Resources. Gender resolves "female".
        GameObject prefab = NpcModels.ResolvePrefab("mara");
        if (prefab == null)
        {
            Debug.LogWarning("[RescueMission] No sister (female civilian) model found; skipping sister.");
            return;
        }

        // Interior anchor: the Cabin asset's walkable room is on the -X / centre side; ~2 m in from the door
        // (door = local +X), on the floor, centred in Z. Transform the local anchor by the cabin's world TRS.
        Vector3 interiorLocal = new Vector3(-2.0f, 0.12f, 0.0f);
        Vector3 sisterPos = GroundSnap(cabinFoot + cabinRot * interiorLocal);

        // She faces the DOOR (toward the incoming player = toward the spawn), so "waiting, watching for you"
        // reads. toSpawn already points from the cabin back toward the spawn.
        Quaternion faceDoor = toSpawn.sqrMagnitude > 1e-4f
            ? Quaternion.LookRotation(toSpawn, Vector3.up)
            : Quaternion.identity;

        sister = Object.Instantiate(prefab, sisterPos, faceDoor);
        if (sister == null) return;
        sister.name = "Sister";

        NpcModels.FixHdrp(sister);          // HDRP re-shade (no white)
        CharacterRig.Apply(sister, false);  // CapsuleCollider + humanoid Animator + CharacterLocomotion (full Locomotion controller)

        sisterAnim = sister.GetComponent<Animator>();  // cached for the reunion Jump trigger

        var sh = sister.GetComponent<Health>();
        if (sh == null) sh = sister.AddComponent<Health>();
        sh.maxHealth = 100f;
        sh.maxShield = 0f;
        sh.faction = Faction.Neutral;       // CombatAI skips Neutral -> the gauntlet guards never shoot her
    }

    // =================================================================================================
    // THE REUNION: jump -> sprint to the player (real locomotion via "Speed") -> a paced, readable,
    // skippable emotional conversation (sister via /tts female voice; you as "You:" subtitles).
    // =================================================================================================

    void BeginReunion()
    {
        if (reunionStarted) return;
        reunionStarted = true;

        // Claim the cinematic stage so other HUD stays out of the way during the beat (released when done).
        StorySequencer.ClaimStage(this);

        // (a) She FREEZES, then JUMPS up the instant she sees you. The Locomotion controller has the "Jump"
        //     trigger; firing it plays the jump state. (CharacterRig already bound the human avatar + controller.)
        if (sisterAnim != null)
        {
            try { sisterAnim.SetTrigger("Jump"); }
            catch (System.Exception e) { Debug.LogWarning($"[RescueMission] Sister Jump trigger skipped: {e.Message}"); }
        }

        // (b) Pre-roll: hold on the jump, then attach the fast run-in mover. reunionStep -1 = pre-roll.
        reunionStep = -1;
        reunionStepAt = Time.time + JumpHold;
        reunionRunDeadline = Time.time + JumpHold + RunTimeout;
        subtitleText = null;

        Debug.Log("[RescueMission] Reunion begun (Jump -> run-in -> conversation).");
    }

    void DriveReunion()
    {
        if (player == null) { FinishReunion(); return; }

        bool skip = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E);

        // ----- pre-roll: jump hold, then sprint to the player until in range (or timeout). -----
        if (reunionStep < 0)
        {
            // After the jump hold, start the fast run-in (drives "Speed" -> the run blend).
            if (Time.time >= reunionStepAt && reunionMover == null && sister != null)
            {
                reunionMover = sister.GetComponent<SisterFollow>();
                if (reunionMover == null) reunionMover = sister.AddComponent<SisterFollow>();
                reunionMover.target = player;
                reunionMover.walkSpeed = ReunionRunSpeed;       // sprint
                reunionMover.followDistance = ReunionRunStop;   // close right up for the hug-range talk
            }

            // Begin the dialogue once she has arrived (or the run timed out, so the beat never stalls).
            bool arrived = sister != null &&
                           FlatDistance(player.position, sister.transform.position) <= ReunionRunStop + 0.4f;
            if (arrived || Time.time >= reunionRunDeadline)
                AdvanceReunion(0);
            return;
        }

        // ----- the scripted conversation: advance on the hold timer or on a skip. -----
        if (skip || Time.time >= reunionStepAt)
            AdvanceReunion(reunionStep + 1);
    }

    // Move to reunion line 'index'. Plays the sister's TTS for spoken lines, sets the subtitle, and arms the
    // hold timer. Past the last line, ends the reunion.
    void AdvanceReunion(int index)
    {
        if (index >= Reunion.Length) { FinishReunion(); return; }

        reunionStep = index;
        ReunionLine line = Reunion[index];

        subtitleText = line.text;
        subtitleIsSister = line.sister;

        if (line.sister) SpeakSister(line.text);   // female voice via /tts; you-lines are subtitle-only

        reunionStepAt = Time.time + Mathf.Max(1.5f, line.hold);
    }

    // The conversation is over: switch the sister to a normal follow, drop the subtitle, then ARM THE REVEAL.
    // We do NOT release the cinematic stage and we do NOT walk back to the shore first: about 4 to 5 seconds
    // after the hug the Curator is revealed in place (we see only him), he delivers his speech, then the fight
    // begins. The stage stays CLAIMED through the reveal so other HUD stays suppressed, exactly as during the
    // reunion; EndRevealAndFight releases it. We reuse the Confront phase as the reveal window so the HUD reads
    // the menacing Confront objective and the RescuePhase enum + MissionHud bindings are untouched.
    void FinishReunion()
    {
        subtitleText = null;
        reunionDone = true;

        // Re-tune the run-in mover to a calm follow (or attach one if the run-in never spun up). She holds at
        // the cabin during the reveal; the calm follow is ready for when the fight breaks.
        if (sister != null)
        {
            if (reunionMover == null)
            {
                reunionMover = sister.GetComponent<SisterFollow>();
                if (reunionMover == null) reunionMover = sister.AddComponent<SisterFollow>();
            }
            reunionMover.target = player;
            reunionMover.walkSpeed = 3.0f;       // calm follow pace (SisterFollow's default)
            reunionMover.followDistance = 2.5f;  // trails a couple of metres back
        }

        // Make the rescued sister a TALKABLE NPC now (NOT at spawn): the [V]/[E] proximity prompt scans for an
        // NpcAgent (LocalPlayer.ScanNearest), so adding one here lets the player Talk/Interrogate her. We add it
        // ONLY after the reunion finished, because NpcAgent.Init swaps her Animator to the breathing NpcIdle
        // controller and removes CharacterLocomotion; adding it earlier would clobber the Jump/run blend she
        // needs for the reunion. She still FOLLOWS after this (SisterFollow walks her transform directly; it does
        // not depend on CharacterLocomotion), she simply plays the calm idle clip instead of a run blend, which
        // is fine for a freed survivor trailing you home. Null-safe + idempotent (one-shot via the agent check).
        MakeSisterTalkable();

        // Arm the reveal: step 0 holds for RevealDelay (the ask: 4 to 5s after the reunion), then BeginReveal.
        // Keep the stage CLAIMED (do NOT ReleaseStage here) so the reveal stays cinematic.
        revealStep = 0;
        revealStepAt = Time.time + RevealDelay;
        SetPhase(RescuePhase.Confront);          // "The Curator is waiting. He will not let you leave."
        Debug.Log("[RescueMission] Reunion complete. Reveal armed (~" + RevealDelay + "s) -> speech -> fight.");
    }

    // Attach an NpcAgent to the rescued sister so the [V]/[E] proximity prompt (LocalPlayer.ScanNearest scans
    // for NpcAgent) works on her: [V] = voice via PlayerVoice, [E] = the existing text interrogate, both routed
    // through AskNpc(NpcId, ...). We reuse MARA's real, server-assigned NpcId so the request lands on Mara's
    // persona in the director (the rescued-Mara persona) instead of a stranger. The id is looked up LIVE from the
    // replicated Npc table by display name (no hardcoded id, since the server assigns ids dynamically); if she is
    // not found we fall back to id 0, so the prompt + her local upbeat TTS still work even without a server route.
    // One-shot (guarded by sisterAgent), null-safe, idempotent. Adds no SpacetimeDB type names (var only), so the
    // file stays free of the SpacetimeDB.Types import (and the bare-Vector3 alias stays unnecessary).
    void MakeSisterTalkable()
    {
        if (sister == null || sisterAgent != null) return;   // no sister, or already talkable

        sisterAgent = sister.GetComponent<NpcAgent>();
        if (sisterAgent == null) sisterAgent = sister.AddComponent<NpcAgent>();

        // Init swaps her Animator to the breathing NpcIdle controller + drops CharacterLocomotion. That is why we
        // only call this AFTER the reunion Jump/run-in. "female" -> the sidecar female voice fallback; her scripted
        // talkable lines below pass the explicit happy SisterVoiceId on top of that.
        try { sisterAgent.Init(ResolveMaraNpcId(), "female"); }
        catch (System.Exception e) { Debug.LogWarning($"[RescueMission] Sister NpcAgent init skipped: {e.Message}"); }

        // A warm, EXCITED post-rescue line in her happy voice the moment she becomes talkable, so the first thing
        // you hear from the freed sister is upbeat (the reunion lines were the emotional beat; this is relief +
        // momentum). No em dashes, no emoji. Uses the SisterVoiceId path (SpeakSister), distinct from the wary NPCs.
        SpeakSister("We are really doing this. Stay close. I have got your back now, and we are getting out together.");

        Debug.Log($"[RescueMission] Sister is now talkable (NpcAgent, female voice). Routed to NpcId={sisterAgent.NpcId}.");
    }

    // Resolve MARA's real, server-assigned NpcId from the replicated Npc table by display name, so talking to the
    // rescued cabin sister routes to Mara's persona in the LLM director. The server assigns ids dynamically (no
    // stable compile-time constant), so we scan the live rows. var-only access keeps the SpacetimeDB type name out
    // of this file. Returns 0 if the connection/row is unavailable (a benign local-only fallback: the [V]/[E]
    // prompt still appears and her local upbeat TTS still plays; only the server round-trip is skipped). Null-safe;
    // never throws (swallows any access exception and falls back to 0).
    static ulong ResolveMaraNpcId()
    {
        try
        {
            if (GameManager.Conn == null) return 0UL;
            foreach (var n in GameManager.Conn.Db.Npc.Iter())
            {
                if (n == null) continue;
                if (!string.IsNullOrEmpty(n.DisplayName) &&
                    n.DisplayName.Trim().Equals("Mara", System.StringComparison.OrdinalIgnoreCase))
                    return n.NpcId;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RescueMission] Mara NpcId lookup failed (sister falls back to local-only talk): {e.Message}");
        }
        return 0UL;
    }

    // =================================================================================================
    // The sister's voice. Mirrors NpcAgent.Speak/SpeakRoutine: POST {"text","gender":"female"} to the /tts
    // sidecar, decode the MPEG, play it on a 2D source. Best-effort + null-safe: if the sidecar is down the
    // subtitle still carries the beat. The sister model is female (NpcModels.ResolveGender("mara")), so the
    // gender literal agrees with the on-screen model. NEVER prints any API key.
    // =================================================================================================

    const string TtsUrl = "http://localhost:8787/tts";
    const int    TtsTimeout = 8;

    void SpeakSister(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (sisterVoice == null)
        {
            sisterVoice = gameObject.AddComponent<AudioSource>();
            sisterVoice.spatialBlend = 0f;   // 2D, the reunion voice is always audible
            sisterVoice.volume = 1f;
            sisterVoice.priority = 0;         // top priority over the music bed
            sisterVoice.playOnAwake = false;
        }
        StopCoroutine(nameof(SpeakSisterRoutine));   // a fresh line supersedes any in-flight request
        StartCoroutine(SpeakSisterRoutine(text));
    }

    System.Collections.IEnumerator SpeakSisterRoutine(string text)
    {
        // The /tts endpoint honors an explicit "voiceId" (priority over gender) AND a "gender" field. We send
        // BOTH: the happy/excited female SisterVoiceId so she sounds upbeat + distinct from the wary NPCs, plus
        // gender:"female" as a fallback for a sidecar that ignores voiceId. Both are fixed literals (no escaping
        // needed); only the line text is escaped.
        string json = "{\"text\":\"" + EscapeJson(text) + "\",\"voiceId\":\"" + SisterVoiceId + "\",\"gender\":\"female\"}";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

        UnityEngine.Networking.UnityWebRequest req = null;
        try
        {
            req = new UnityEngine.Networking.UnityWebRequest(TtsUrl, "POST")
            {
                uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body),
                downloadHandler = new UnityEngine.Networking.DownloadHandlerAudioClip(TtsUrl, AudioType.MPEG),
                timeout = TtsTimeout,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            ((UnityEngine.Networking.DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RescueMission] Sister TTS build failed (voice skipped): {e.Message}");
            req?.Dispose();
            yield break;
        }

        yield return req.SendWebRequest();

        if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            AudioClip clip = null;
            try { clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(req); }
            catch (System.Exception e) { Debug.LogWarning($"[RescueMission] Sister TTS decode failed: {e.Message}"); }
            if (clip != null && clip.loadState == AudioDataLoadState.Loaded && sisterVoice != null)
            {
                sisterVoice.clip = clip;
                sisterVoice.Play();
            }
        }
        else
        {
            // Sidecar down / timeout / HTTP error: stay silent. The subtitle still carries the beat.
            Debug.LogWarning($"[RescueMission] Sister TTS {req.result}: {req.error} (voice skipped).");
        }

        req.Dispose();
    }

    // =================================================================================================
    // ACT VI, the Curator BODY. Ezra "The Curator" manifests: a hand-picked bodyguard mesh, uniform-scaled up
    // and dark-tinted, with his Health (100 GREEN + 100 BLUE = 200 total, down from 500), placed at the
    // DETERMINISTIC boss point so every client sees him in the same spot. He stands INERT here (no CombatAI, no
    // weapon, no elites) so the reveal camera can frame ONLY him while he speaks; ArmCurator wires the fight.
    // =================================================================================================

    void SpawnCuratorBody()
    {
        if (bossBodyStanding) return;
        bossBodyStanding = true;
        if (player == null) { SetPhase(RescuePhase.Victory); return; }

        // DETERMINISTIC boss placement: past the deterministic landing along the fixed mission heading, so he
        // blocks the way home at the SAME world position on every client (NOT derived from the local player).
        // The ProBuilder rock COLLIDERS are removed, so a blind ground-snap could drop him half inside a visible
        // boulder. Route the candidate through RockClearance.ClearOfRocks FIRST (it ring-searches out of nearby
        // big rock RENDERER bounds, the only way to detect rocks now that the colliders are gone), THEN GroundSnap
        // so the final Y still sits on terrain. Order matters: clear horizontally, then snap vertically.
        // ClearOfRocks is scene-deterministic (a fixed ring search over the same scene rock renderers), so every
        // client still agrees on the boss position; the deterministic-placement guarantee above is preserved.
        Vector3 bossCandidate = landingPos + missionHeading * BossBlockDist;
        Vector3 bossPos = GroundSnap(RockClearance.ClearOfRocks(bossCandidate, BossClearRadius));

        // He faces the player for the reveal framing + the fight feel (FACING may differ per client; only the
        // POSITION needs to be deterministic). Fall back to facing back down the heading if the player is gone.
        Vector3 toPlayer = player.position - bossPos; toPlayer.y = 0f;
        Quaternion rot = toPlayer.sqrMagnitude > 1e-4f
            ? Quaternion.LookRotation(toPlayer.normalized, Vector3.up)
            : Quaternion.LookRotation(-missionHeading, Vector3.up);

        // Distinct model: bodyguard variant 3 (SkelMesh_Bodyguard_04), reserved for the boss.
        GameObject prefab = BodyguardModels.ResolvePrefab(3);
        if (prefab == null)
        {
            Debug.LogWarning("[RescueMission] No Curator model; boss skipped, resolving to Victory.");
            SetPhase(RescuePhase.Victory);
            return;
        }

        boss = Object.Instantiate(prefab, bossPos, rot);
        if (boss == null) { SetPhase(RescuePhase.Victory); return; }
        boss.name = "Ezra_TheCurator";
        BossPosition = bossPos;   // read-only mirror for the map agent (boss pin during BossFight)

        BodyguardModels.FixHdrp(boss);
        CharacterRig.Apply(boss, false);   // wires the locomotion idle so he stands (no T-pose) without a brain

        // BOSS LOOK: a bigger silhouette + a cold near-black tint ("the collector in black").
        boss.transform.localScale = Vector3.one * BossScale;
        TintBoss(boss, new Color(0.10f, 0.10f, 0.12f, 1f));

        // BOSS HP: 100 GREEN (maxHealth) + 100 BLUE (maxShield) = 200 total (down from 500). ResetFull seeds
        // CurrentHealth = 100 and CurrentShield = 100 before any damage can land; the blue shield drains first,
        // so it must be stripped before his green health drops.
        bossHealth = boss.GetComponent<Health>();
        if (bossHealth == null) bossHealth = boss.AddComponent<Health>();
        bossHealth.maxHealth = BossHealth;     // 100 green
        bossHealth.maxShield = BossShield;     // 100 blue
        bossHealth.faction   = Faction.Bodyguard;
        bossHealth.ResetFull();                // apply the new maxes (100 green + 100 blue) before any damage
        bossHealth.OnDied += HandleBossDied;   // Ezra death -> Victory

        Debug.Log($"[RescueMission] Ezra 'The Curator' BODY revealed at {bossPos} " +
                  $"({BossHealth} green + {BossShield} blue = {BossHealth + BossShield} total). Speaking.");
    }

    // =================================================================================================
    // ACT VI -> VII, arm the Curator. Once the reveal speech ends, give him his weapon + aggressive CombatAI
    // and spawn his elites, so the fight begins. Split from the body spawn so "we see him only" holds during
    // the reveal (no gun, no elites on screen). All on the proven hunter pipeline. Null-safe.
    // =================================================================================================

    void ArmCurator()
    {
        bossSpawned = true;
        if (boss == null || bossHealth == null) { SetPhase(RescuePhase.Victory); return; }

        var weapon = WeaponRig.Equip(boss);

        var ai = boss.GetComponent<CombatAI>();
        if (ai == null) ai = boss.AddComponent<CombatAI>();
        ai.Init(Faction.Bodyguard, weapon, bossHealth);
        // AGGRESSIVE boss tuning (all public CombatAI fields).
        ai.sightRange    = 80f;    // commits to the player across the clearing (default 45)
        ai.moveSpeed     = 5.0f;   // closes faster than a hunter (default 3.5)
        ai.aiFireRate    = 3.0f;   // shoots faster than a grunt (default 1.8)
        ai.aimSpread     = 5f;     // tighter aim than grunts (default 10), more dangerous
        ai.reactionDelay = 0.4f;   // snaps onto the player quicker (default 0.85)
        ai.despawnDelay  = 12f;    // corpse lingers under the victory beat

        SpawnElites(EliteCount, boss.transform.position);
        SpeakCurator("You should have stayed a tourist. Now I add you to the collection.");

        Debug.Log($"[RescueMission] Ezra armed + {EliteCount} elites. BossFight engaged.");
    }

    // Three elite guards flanking Ezra. The proven SpawnHunter body, then a small upgrade (tougher Health,
    // tighter aim) so they read a step above the grunts. They are Faction.Bodyguard -> auto-hunt the player.
    void SpawnElites(int count, Vector3 bossPos)
    {
        if (player == null) return;
        int placed = 0;
        for (int i = 0; i < count; i++)
        {
            float ang = (i / (float)count) * Mathf.PI * 2f;
            Vector3 xz = bossPos + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * EliteRing;
            // Same renderer-bounds rock clearance as the boss (colliders are gone): nudge each elite out of any
            // nearby big rock RENDERER bounds, THEN ground-snap, so the flank does not land inside a boulder.
            Vector3 pos = GroundSnap(RockClearance.ClearOfRocks(xz, EliteClearRadius));

            Vector3 toPlayer = player.position - pos; toPlayer.y = 0f;
            Quaternion rot = toPlayer.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(toPlayer.normalized, Vector3.up)
                : Quaternion.identity;

            var go = SpawnHunter(i, pos, rot);
            if (go == null) continue;
            placed++;
            go.name = $"Elite_{i:00}";

            // Elite upgrade over the grunt baseline.
            var h = go.GetComponent<Health>();
            if (h != null) { h.maxHealth = EliteHealth; h.maxShield = 0f; h.ResetFull(); }
            var ai = go.GetComponent<CombatAI>();
            if (ai != null) { ai.aimSpread = 7f; ai.reactionDelay = 0.6f; }
        }
        Debug.Log($"[RescueMission] Spawned {placed}/{count} elites ({EliteHealth} HP) around Ezra.");
    }

    // One guard, with the EXACT proven combat pipeline (mirrors ExpeditionPopulation.SpawnBodyguard): model
    // -> HDRP fix -> CharacterRig.Apply -> Health(100, Bodyguard) -> WeaponRig.Equip -> CombatAI. Used by
    // SpawnElites for the boss flank (the trek gauntlet itself is owned by ExpeditionPopulation). Returns the
    // spawned body (so callers can upgrade it), or null on failure.
    GameObject SpawnHunter(int variant, Vector3 pos, Quaternion rot)
    {
        GameObject prefab = BodyguardModels.ResolvePrefab(variant);
        if (prefab == null)
        {
            Debug.LogWarning($"[RescueMission] No bodyguard model for variant {variant}; skipping guard.");
            return null;
        }

        var go = Object.Instantiate(prefab, pos, rot);
        if (go == null) return null;
        go.name = $"Guard_{variant:00}";

        BodyguardModels.FixHdrp(go);
        CharacterRig.Apply(go, false);

        var health = go.GetComponent<Health>();
        if (health == null) health = go.AddComponent<Health>();
        health.maxHealth = 100f;
        health.maxShield = 0f;
        health.faction = Faction.Bodyguard;

        var weapon = WeaponRig.Equip(go);

        var ai = go.GetComponent<CombatAI>();
        if (ai == null) ai = go.AddComponent<CombatAI>();
        ai.Init(Faction.Bodyguard, weapon, health);   // hostile to the Survivalist player; player is nearest enemy

        return go;
    }

    // Dark-tint every renderer on the boss (mirrors NetworkedWorld.Tint). Guarded by HasProperty so it is safe
    // across HDRP/Lit and any other shader. Null-safe.
    static void TintBoss(GameObject go, Color tint)
    {
        if (go == null) return;
        var rends = go.GetComponentsInChildren<Renderer>(true);
        if (rends == null) return;
        for (int i = 0; i < rends.Length; i++)
        {
            var r = rends[i];
            if (r == null) continue;
            var mats = r.materials;   // instance materials (so we never edit the shared asset)
            if (mats == null) continue;
            for (int m = 0; m < mats.Length; m++)
            {
                var mat = mats[m];
                if (mat == null) continue;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            }
        }
    }

    // =================================================================================================
    // ACT VII, Victory: Ezra is down. Fired exactly once via his Health.OnDied (race-free per Health). (KEPT.)
    // =================================================================================================

    void HandleBossDied(Health victim)
    {
        if (Phase == RescuePhase.Victory) return;   // fire once
        if (bossHealth != null) bossHealth.OnDied -= HandleBossDied;
        SetPhase(RescuePhase.Victory);              // "You got her out. You made it home."
        SpeakCurator("...how. It was mine. All of it was mine.");  // composure broken
        PlayAlarm();                                // a clean final sting under the victory card
        Debug.Log("[RescueMission] Ezra down. Victory.");
    }

    // =================================================================================================
    // The Curator's voice. POST {"text","voiceId"} to the /tts sidecar (a distinct, menacing voice). Same
    // best-effort, null-safe pattern as SpeakSister. (KEPT.)
    // =================================================================================================

    void SpeakCurator(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (curatorVoice == null)
        {
            curatorVoice = gameObject.AddComponent<AudioSource>();
            curatorVoice.spatialBlend = 0f;   // 2D, the PA voice is always audible, non-diegetic
            curatorVoice.volume = 1f;
            curatorVoice.priority = 0;         // top priority over the music bed
            curatorVoice.playOnAwake = false;
        }
        StopCoroutine(nameof(SpeakCuratorRoutine));   // a fresh line supersedes any in-flight request
        StartCoroutine(SpeakCuratorRoutine(text));
    }

    System.Collections.IEnumerator SpeakCuratorRoutine(string text)
    {
        string json = "{\"text\":\"" + EscapeJson(text) + "\",\"voiceId\":\"" + CuratorVoiceId + "\"}";
        byte[] body = System.Text.Encoding.UTF8.GetBytes(json);

        UnityEngine.Networking.UnityWebRequest req = null;
        try
        {
            req = new UnityEngine.Networking.UnityWebRequest(TtsUrl, "POST")
            {
                uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body),
                downloadHandler = new UnityEngine.Networking.DownloadHandlerAudioClip(TtsUrl, AudioType.MPEG),
                timeout = TtsTimeout,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            ((UnityEngine.Networking.DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[RescueMission] Curator TTS build failed (voice skipped): {e.Message}");
            req?.Dispose();
            yield break;
        }

        yield return req.SendWebRequest();

        if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
        {
            AudioClip clip = null;
            try { clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(req); }
            catch (System.Exception e) { Debug.LogWarning($"[RescueMission] Curator TTS decode failed: {e.Message}"); }
            if (clip != null && clip.loadState == AudioDataLoadState.Loaded && curatorVoice != null)
            {
                curatorVoice.clip = clip;
                curatorVoice.Play();
            }
        }
        else
        {
            // Sidecar down / timeout / HTTP error: stay silent. The HUD line still carries the beat.
            Debug.LogWarning($"[RescueMission] Curator TTS {req.result}: {req.error} (voice skipped).");
        }

        req.Dispose();
    }

    // The alarm / boss sting. Load the dread stinger directly and play it through a throwaway 2D AudioSource,
    // falling back to AudioDirector.PlayStinger() (a no-op if no host/clip). Build-safe, null-safe. (KEPT.)
    void PlayAlarm()
    {
        AudioClip clip = null;
        try { clip = Resources.Load<AudioClip>("Audio/dread_stinger"); }
        catch { clip = null; }

        if (clip == null) { AudioDirector.PlayStinger(); return; }

        var go = new GameObject("RescueAlarm");
        var src = go.AddComponent<AudioSource>();
        src.spatialBlend = 0f;   // 2D, always audible
        src.volume = 0.9f;
        src.priority = 0;        // urgent: top priority so it never gets culled under the music bed
        src.PlayOneShot(clip);
        Object.Destroy(go, clip.length + 0.25f);
    }

    // Minimal JSON string escaping (mirrors NpcAgent.EscapeJson) so a line with quotes never breaks the body.
    static string EscapeJson(string s)
    {
        var sb = new System.Text.StringBuilder((s?.Length ?? 0) + 16);
        if (s != null)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
        }
        return sb.ToString();
    }

    // ---- on-screen reunion subtitle + the Victory card (Barlow, backing panel + shadow, terse) -------

    void OnGUI()
    {
        // IN-GAME GATE (single chokepoint for the waypoint + reunion subtitle + reveal caption + victory card):
        // suppress entirely during auth/lobby (GameplayActive false) and the deploy cutscene (Active true).
        if (!NetworkedWorld.GameplayActive || DeployCutscene.Active) return;

        // Act VII: the clean full-screen victory card (over a dark scrim). Takes priority over everything else.
        if (Phase == RescuePhase.Victory) { DrawVictoryCard(); return; }

        // The REVEAL caption: while the Curator is framed + speaking (Confront phase, reveal step 1), a terse
        // name card so the player reads who they are looking at. No em dash, no emoji.
        if (Phase == RescuePhase.Confront && revealStep == 1)
            DrawRevealCaption();

        // The reunion subtitle (lower third): sister lines in SemiBold, "You:" lines in Regular.
        if (Phase == RescuePhase.Reunion && !string.IsNullOrEmpty(subtitleText))
            DrawReunionSubtitle();

        // The cabin waypoint (distance + direction) + the spawn diagnostic, so the cabin can always be found.
        DrawCabinWaypoint();
    }

    // The reveal name card: a terse lower-third title over a subtle scrim while the Curator is framed + speaking.
    // Barlow via StorySequencer (null-safe fallback to the GUI skin font). No em dash, no emoji.
    void DrawRevealCaption()
    {
        Font titleFont = StorySequencer.GetFont(StorySequencer.Weight.Bold);
        Font subFont   = StorySequencer.GetFont(StorySequencer.Weight.Regular);

        float cx = Screen.width / 2f;
        float cy = Screen.height - 150f;

        DrawCardLine(new Rect(cx - 400f, cy, 800f, 56f), "THE CURATOR", 46, titleFont, Color.white);
        DrawCardLine(new Rect(cx - 400f, cy + 56f, 800f, 32f), "Ezra", 22, subFont,
                     new Color(0.82f, 0.18f, 0.16f, 1f));
    }

    // The reunion subtitle: a centred lower-third line with a semi-opaque black backing panel + a drop shadow,
    // in Barlow (SemiBold for the sister; Regular for "You:"). Null-safe (falls back to the GUI skin font).
    void DrawReunionSubtitle()
    {
        if (sisterFont == null) sisterFont = StorySequencer.GetFont(StorySequencer.Weight.SemiBold);
        if (youFont == null)    youFont    = StorySequencer.GetFont(StorySequencer.Weight.Regular);

        string label = subtitleIsSister ? subtitleText : ("You: " + subtitleText);
        Font font = subtitleIsSister ? sisterFont : youFont;
        int fontSize = subtitleIsSister ? 24 : 22;

        const float w = 760f, h = 44f;
        float x = Screen.width / 2f - w / 2f;
        float y = Screen.height - 140f;

        // backing panel (semi-opaque black)
        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(x - 18f, y - 10f, w + 36f, h + 20f), Texture2D.whiteTexture);
        GUI.color = prev;

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
        };
        if (font != null) style.font = font;

        // drop shadow, then the text on top (sister bright white; "You:" a touch cooler so they read distinct).
        var shadow = new GUIStyle(style);
        shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(new Rect(x + 2f, y + 2f, w, h), label, shadow);
        style.normal.textColor = subtitleIsSister ? Color.white : new Color(0.82f, 0.86f, 0.92f, 1f);
        GUI.Label(new Rect(x, y, w, h), label, style);

        // a small skip hint under the line.
        var hint = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = false,
        };
        if (youFont != null) hint.font = youFont;
        hint.normal.textColor = new Color(0.75f, 0.75f, 0.78f, 0.7f);
        GUI.Label(new Rect(x, y + h + 6f, w, 18f), "Space to continue", hint);
    }

    // The Victory card: a full-screen dark scrim with a terse centred title + subtitle in Barlow. (KEPT.)
    void DrawVictoryCard()
    {
        const string title = "SHE IS FREE";
        const string subtitle = "You made it home.";

        var prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.78f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = prev;

        Font titleFont = StorySequencer.GetFont(StorySequencer.Weight.Bold);
        Font subFont   = StorySequencer.GetFont(StorySequencer.Weight.Regular);

        float cx = Screen.width / 2f;
        float cy = Screen.height / 2f;

        DrawCardLine(new Rect(cx - 400f, cy - 60f, 800f, 64f), title, 52, titleFont, Color.white);
        DrawCardLine(new Rect(cx - 400f, cy + 16f, 800f, 40f), subtitle, 24, subFont,
                     new Color(0.85f, 0.85f, 0.88f, 1f));
    }

    // One centred card line with a drop shadow. Null-safe. (KEPT.)
    static void DrawCardLine(Rect rect, string text, int size, Font font, Color color)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = false,
        };
        if (font != null) style.font = font;

        var shadow = new GUIStyle(style);
        shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, shadow);

        style.normal.textColor = color;
        GUI.Label(rect, text, style);
    }

    // ---- shared helpers (copied per project convention; consistency > DRY, all build-safe) ----------

    // XZ-only distance so standing next to the cabin on uneven ground still registers.
    static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Down-raycast ground-snap (lowest hit = terrain, not canopy). Same pattern as NetworkedWorld.V /
    // CombatSpawner.GroundSnap / ExpeditionPopulation.GroundSnap. Falls back to the requested point.
    static Vector3 GroundSnap(Vector3 pos)
    {
        var hits = Physics.RaycastAll(new Vector3(pos.x, pos.y + 400f, pos.z), Vector3.down, 3000f,
            ~0, QueryTriggerInteraction.Ignore);
        if (hits != null && hits.Length > 0)
        {
            float groundY = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].point.y < groundY) groundY = hits[i].point.y;
            if (groundY < float.MaxValue) return new Vector3(pos.x, groundY + 0.05f, pos.z);
        }
        return pos;
    }
}

// SisterFollow. a minimal chase mover for the sister. Walks her transform toward the player (driving
// CharacterLocomotion's "Speed" via the transform delta, so the locomotion blend tree animates real movement
// the WALK end at the default speed, the RUN end when the reunion bumps walkSpeed to a sprint), stops a short
// distance off so she never shoves the player, and ground-snaps each step so she follows the terrain. Pure
// UnityEngine, null-safe; never throws. One class per name.
public class SisterFollow : MonoBehaviour
{
    [Tooltip("Who she follows (the local player).")]
    public Transform target;

    [Tooltip("Move speed (m/s). Default = follow walk; the reunion bumps this to a sprint for the run-in.")]
    public float walkSpeed = 3.0f;

    [Tooltip("Stop this close to the player, then idle (metres).")]
    public float followDistance = 2.5f;

    [Tooltip("Turn rate toward the player (deg/s).")]
    public float turnRate = 220f;

    void Update()
    {
        if (target == null) return;

        Vector3 flat = target.position - transform.position; flat.y = 0f;
        float dist = flat.magnitude;
        if (dist <= followDistance) return;        // close enough -> idle (Speed decays to 0 -> idle clip)
        if (flat.sqrMagnitude < 1e-4f) return;

        Vector3 dir = flat.normalized;

        // Smooth yaw toward the player.
        Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnRate * Time.deltaTime);

        // Step horizontally, then ground-snap Y so she follows the terrain (the transform delta feeds the
        // Animator "Speed" via CharacterLocomotion -> the locomotion blend tree plays a walk or a run).
        Vector3 next = transform.position + dir * walkSpeed * Time.deltaTime;
        transform.position = GroundSnap(next);
    }

    // Same down-raycast ground-snap used across the project (lowest hit = terrain, not canopy).
    static Vector3 GroundSnap(Vector3 pos)
    {
        var hits = Physics.RaycastAll(new Vector3(pos.x, pos.y + 400f, pos.z), Vector3.down, 3000f,
            ~0, QueryTriggerInteraction.Ignore);
        if (hits != null && hits.Length > 0)
        {
            float groundY = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
                if (hits[i].point.y < groundY) groundY = hits[i].point.y;
            if (groundY < float.MaxValue) return new Vector3(pos.x, groundY + 0.05f, pos.z);
        }
        return pos;
    }
}
