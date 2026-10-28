using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace SquadPTSD
{
    [DefOf]
    public static class SQP_DefOf
    {
        public static HediffDef SQP_WarHardened;
        public static MentalStateDef SQP_MercyKilling;
        public static JobDef SQP_GoodNight;

        // From the Sociopath mod (required), looked up by name so its code can change freely.
        public static TraitDef SP_Sociopath;

        static SQP_DefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SQP_DefOf));
        }
    }

    /// <summary>Fixed numbers. No settings: this mod has no switches.</summary>
    public static class Tuning
    {
        // Hardening: chance that a sad-wandering breakdown hardens a colonist who has fought with the squad.
        public const float HardenChance = 0.5f;

        // "Has fought": at least this much combat time (half an in-game hour), counted by this mod.
        public const int MinCombatTicks = 1250;

        // A colonist is in combat if they attacked, aimed at a target, or were hurt within the last in-game hour.
        public const int CombatWindowTicks = 2500;

        // Sociopath mod trait degrees.
        public const int DegreeCallous = 1;
        public const int DegreeSociopath = 2;

        // Beyond saving: bleeding to death within this long (4 in-game hours), or dying of a condition within a day.
        public const int BleedOutTicks = 10000;
        public const float ConditionDays = 1f;

        // A disease only counts if death is this close. Without it, a slow chronic illness with no immunity
        // to build (heart artery blockage, for example) would count as beyond saving years in advance.
        public const float DiseaseDays = 2f;

        // How often war-hardened colonists look for someone beyond saving, and the pause after an episode.
        public const int CheckInterval = 250;
        public const int CooldownTicks = 2500;

        // The pause before the finisher, as in Pawnicide (three seconds at normal speed).
        public const int PauseTicks = 180;
    }

    [StaticConstructorOnStartup]
    public static class SQP
    {
        static SQP()
        {
            new Harmony("squad.ptsd").PatchAll();
            Log.Message("[Squad PTSD] Loaded.");
        }

        public static int SociopathDegree(Pawn p)
        {
            Trait t = p?.story?.traits?.GetTrait(SQP_DefOf.SP_Sociopath);
            return t == null ? 0 : t.Degree;
        }

        public static bool InCombat(Pawn p)
        {
            Pawn_MindState mind = p?.mindState;
            if (mind == null || p.Dead)
            {
                return false;
            }
            int now = Find.TickManager.TicksGame;
            return now - mind.lastEngageTargetTick < Tuning.CombatWindowTicks ||
                   now - mind.lastAttackTargetTick < Tuning.CombatWindowTicks ||
                   now - mind.lastHarmTick < Tuning.CombatWindowTicks;
        }

        public static GameComponent_SquadPTSD Tracker => Current.Game?.GetComponent<GameComponent_SquadPTSD>();

        public static bool IsWarHardened(Pawn p) =>
            p?.health?.hediffSet != null && p.health.hediffSet.HasHediff(SQP_DefOf.SQP_WarHardened);

        /// <summary>
        /// Beyond saving: downed, and dying of something that isn't being beaten.
        /// Bleeding out within a few hours, losing to a disease, or a condition that will kill within a day.
        /// </summary>
        public static bool BeyondSaving(Pawn p) => BeyondSavingReason(p) != null;

        /// <summary>Why this pawn is beyond saving ("bleeding out", "losing to plague"...), or null if they aren't.</summary>
        public static string BeyondSavingReason(Pawn p)
        {
            if (p == null || p.Dead || !p.Downed || p.health?.hediffSet == null)
            {
                return null;
            }
            if (HealthUtility.TicksUntilDeathDueToBloodLoss(p) < Tuning.BleedOutTicks)
            {
                return "bleeding out";
            }
            List<Hediff> hediffs = p.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                if (IsKilling(p, hediffs[i], out bool disease))
                {
                    return (disease ? "losing to " : "dying of ") + hediffs[i].def.label;
                }
            }
            return null;
        }

        /// <summary>
        /// Lethal conditions that can be fixed by moving, feeding or waiting. Never counted, whatever other mods do to them.
        /// Blood loss is handled by the bleeding rule instead.
        /// </summary>
        private static readonly HashSet<string> Fixable = new HashSet<string>
        {
            "Hypothermia", "Heatstroke", "Malnutrition", "ToxicBuildup", "DrugOverdose", "BloodLoss"
        };

        private static bool IsKilling(Pawn p, Hediff h, out bool disease)
        {
            disease = false;
            float lethal = h.def.lethalSeverity;
            if (lethal <= 0f || Fixable.Contains(h.def.defName) || !(h is HediffWithComps hc))
            {
                return false;
            }
            float perDay = 0f;
            HediffComp_Immunizable immunizable = null;
            foreach (HediffComp comp in hc.comps)
            {
                if (comp is HediffComp_SeverityModifierBase modifier)
                {
                    perDay += modifier.SeverityChangePerDay();
                }
                if (comp is HediffComp_Immunizable im)
                {
                    immunizable = im;
                }
            }
            if (perDay <= 0f)
            {
                return false;
            }
            float daysToDeath = (lethal - h.Severity) / perDay;

            if (immunizable != null)
            {
                // A disease: losing if it will kill them before their immunity finishes building.
                disease = true;
                if (immunizable.FullyImmune)
                {
                    return false;
                }
                if (daysToDeath > Tuning.DiseaseDays)
                {
                    return false;
                }
                ImmunityRecord record = p.health.immunity.GetImmunityRecord(h.def);
                float immunityPerDay = record == null ? 0f : record.ImmunityChangePerTick(p, true, h) * GenDate.TicksPerDay;
                float daysToImmune = immunityPerDay > 0f ? (1f - record.immunity) / immunityPerDay : float.MaxValue;
                return daysToDeath < daysToImmune;
            }
            // Any other lethal condition: dying within a day.
            return daysToDeath < Tuning.ConditionDays;
        }

        /// <summary>The nearest adult on the map who is downed, beyond saving, and reachable.</summary>
        public static Pawn FindTarget(Pawn killer)
        {
            if (killer == null || !killer.Spawned || killer.Downed)
            {
                return null;
            }
            Pawn best = null;
            float bestDist = float.MaxValue;
            IReadOnlyList<Pawn> all = killer.Map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (p == killer || p.RaceProps == null || !p.RaceProps.Humanlike || p.DevelopmentalStage != DevelopmentalStage.Adult)
                {
                    continue;
                }
                // Our own people, and only ours.
                //
                // This used to search every pawn on the map, so a hardened colonist would cross a
                // contested battlefield -- through Danger.Deadly, see below -- to put down a downed
                // raider. That is not mercy and it is not what this is for; it is a colonist
                // walking into a firefight to finish somebody who was already no threat.
                // Prisoners are excluded too: a prisoner bleeding out in the cells is the player's
                // decision and Guilt already has views about it.
                if (p.Faction != Faction.OfPlayer || p.HostFaction != null)
                {
                    continue;
                }
                if (!BeyondSaving(p))
                {
                    continue;
                }
                float dist = p.Position.DistanceToSquared(killer.Position);
                if (dist >= bestDist || !killer.CanReach(p, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                best = p;
                bestDist = dist;
            }
            return best;
        }

        /// <summary>
        /// Anything the Sociopath trait declares itself incompatible with.
        ///
        /// Read from the trait def rather than listed here, so it follows whatever conflicts are
        /// actually declared. With the Machiavelli mod installed that includes Machiavellian, which
        /// matters because the game's own GainTrait does not enforce conflicts: converting one
        /// would leave the colonist holding both traits, and both of them steer mental breaks.
        /// </summary>
        public static bool HasConflictingTrait(Pawn p)
        {
            TraitDef socio = SQP_DefOf.SP_Sociopath;
            List<TraitDef> conflicts = socio?.conflictingTraits;
            if (conflicts == null || p?.story?.traits == null)
            {
                return false;
            }
            for (int i = 0; i < conflicts.Count; i++)
            {
                if (conflicts[i] != null && conflicts[i] != socio && p.story.traits.HasTrait(conflicts[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Harden a colonist: they become a Sociopath (Callous is upgraded) and are marked war-hardened.</summary>
        public static void Harden(Pawn p)
        {
            // Nothing should reach here holding a conflicting trait, but this is the point of no
            // return for a colonist, so it is checked again rather than assumed.
            if (HasConflictingTrait(p))
            {
                return;
            }
            TraitSet traits = p.story.traits;
            // Belt and braces with the gate in CanBeHardened. A Trait with a null def is accepted
            // by GainTrait without complaint and only throws later, from somewhere else entirely,
            // by which time it is in the save.
            if (SQP_DefOf.SP_Sociopath == null)
            {
                return;
            }
            Trait existing = traits.GetTrait(SQP_DefOf.SP_Sociopath);
            if (existing != null)
            {
                traits.RemoveTrait(existing);
            }
            traits.GainTrait(new Trait(SQP_DefOf.SP_Sociopath, Tuning.DegreeSociopath));
            p.needs?.AddOrRemoveNeedsAsAppropriate();
            if (!IsWarHardened(p))
            {
                p.health.AddHediff(SQP_DefOf.SQP_WarHardened);
            }
            Find.LetterStack.ReceiveLetter($"{p.LabelShort} came back hardened",
                $"Something in {p.LabelShort} went quiet during that breakdown. {p.LabelShort} is now a Sociopath, and war-hardened.\n\n" +
                $"From now on, {p.LabelShort} decides for themselves who is beyond saving. When someone nearby is downed and dying, {p.LabelShort} will put them out of their misery.",
                LetterDefOf.NegativeEvent, p);
        }

        public static bool CanBeHardened(Pawn p)
        {
            // Without Sociopath there is nothing to harden anyone into.
            //
            // The dependency declared in About.xml is advisory -- RimWorld warns and offers to
            // subscribe, it does not refuse to launch -- so this genuinely runs with SP_Sociopath
            // bound to null. Every guard below then quietly passes: GetTrait(null) returns null so
            // the degree reads 0, and HasConflictingTrait finds no conflicts list and says there
            // are none. Hardening proceeded to hand the colonist a Trait with a null def, which
            // the game accepts, stores, saves, and then throws on the first read of.
            if (SQP_DefOf.SP_Sociopath == null)
            {
                return false;
            }
            if (p?.story?.traits == null || p.Dead || !p.IsColonist || !p.RaceProps.Humanlike)
            {
                return false;
            }
            if (SociopathDegree(p) >= Tuning.DegreeSociopath || p.story.traits.HasTrait(TraitDefOf.Kind) || p.story.traits.HasTrait(TraitDefOf.Psychopath))
            {
                return false;
            }
            if (HasConflictingTrait(p))
            {
                return false;
            }
            GameComponent_SquadPTSD tracker = Tracker;
            return tracker != null && tracker.CombatTicks(p) >= Tuning.MinCombatTicks;
        }
    }

    // A sad-wandering breakdown can harden a colonist who has fought with the squad.
    [HarmonyPatch(typeof(MentalState), nameof(MentalState.RecoverFromState))]
    public static class Patch_RecoverFromState
    {
        public static void Postfix(MentalState __instance)
        {
            Pawn p = __instance.pawn;
            if (__instance.def?.defName != "Wander_Sad" || !SQP.CanBeHardened(p))
            {
                return;
            }
            if (Rand.Chance(Tuning.HardenChance))
            {
                SQP.Harden(p);
            }
        }
    }

    // ---------- The mercy-killing episode ----------

    public class MentalStateWorker_MercyKilling : MentalStateWorker
    {
        public override bool StateCanOccur(Pawn pawn)
        {
            return base.StateCanOccur(pawn) && SQP.FindTarget(pawn) != null;
        }
    }

    public class MentalState_MercyKilling : MentalState
    {
        public Pawn target;
        public string reason;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref reason, "reason");
        }

        public override void PreStart()
        {
            base.PreStart();
            target = SQP.FindTarget(pawn);
            reason = SQP.BeyondSavingReason(target);
        }

        public override void MentalStateTick(int delta)
        {
            base.MentalStateTick(delta);
            if (pawn.MentalState != this || !pawn.IsHashIntervalTick(60, delta))
            {
                return;
            }
            if (target == null || target.Dead || !target.Spawned || target.Map != pawn.Map)
            {
                RecoverFromState();
            }
            else if (!SQP.BeyondSaving(target))
            {
                Messages.Message($"{target.LabelShort} is no longer beyond saving. {pawn.LabelShort} stands down.",
                    pawn, MessageTypeDefOf.PositiveEvent);
                RecoverFromState();
            }
            else if (!pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly))
            {
                RecoverFromState();
            }
        }

        public override TaggedString GetBeginLetterText()
        {
            if (target == null)
            {
                return "";
            }
            return def.beginLetter.Formatted(pawn.NameShortColored, target.NameShortColored, reason ?? "dying", pawn.Named("PAWN"), target.Named("TARGET"))
                .AdjustedFor(pawn).Resolve().CapitalizeFirst();
        }
    }

    public class JobGiver_MercyKill : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!(pawn.MentalState is MentalState_MercyKilling state) || state.target == null || !SQP.BeyondSaving(state.target))
            {
                return null;
            }
            if (!pawn.CanReach(state.target, PathEndMode.Touch, Danger.Deadly))
            {
                return null;
            }
            return JobMaker.MakeJob(SQP_DefOf.SQP_GoodNight, state.target);
        }
    }

    /// <summary>
    /// The Good Night finisher, as in Pawnicide: go to them, pause three seconds, "Good night princess",
    /// then the game's own hunting finisher. This copy is for the mercy-killing episode only;
    /// Pawnicide's drafted commands are untouched.
    /// </summary>
    public class JobDriver_MercyGoodNight : JobDriver
    {
        private Pawn Victim => job.targetA.Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        /// <summary>
        /// Everything that has to still be true for this to go ahead.
        ///
        /// Checked continuously, including through the pause, and once more in the instant before
        /// the finisher runs. The episode itself only re-examines the victim about once a second,
        /// which left a window where someone could be treated, stay down, and be killed anyway.
        /// Being saved and then executed is the worst outcome this mod could produce, so the
        /// medical check is repeated here rather than trusted from a moment ago.
        /// </summary>
        private bool StillValid()
        {
            Pawn v = Victim;
            if (v == null || v == pawn || v.Dead || !v.Downed || !v.Spawned || v.Map != pawn.Map)
            {
                return false;
            }
            // The episode must still be running, and still be about this person.
            if (!(pawn.MentalState is MentalState_MercyKilling state) || state.target != v)
            {
                return false;
            }
            // And they must still be beyond saving. Treat them and this stops.
            return SQP.BeyondSaving(v);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !StillValid());
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil wait = Toils_General.WaitWith(TargetIndex.A, Tuning.PauseTicks, true);
            wait.tickAction = delegate { pawn.rotationTracker.FaceTarget(job.targetA); };
            wait.FailOn(() => !pawn.CanReachImmediate(job.targetA, PathEndMode.Touch));
            // Re-checked every tick of the pause, not only at the end of it.
            wait.FailOn(() => !StillValid());
            yield return wait;
            Toil finish = new Toil { defaultCompleteMode = ToilCompleteMode.Instant };
            finish.initAction = delegate
            {
                Pawn victim = Victim;
                if (!StillValid() || !pawn.CanReachImmediate(victim, PathEndMode.Touch))
                {
                    return;
                }
                Map map = pawn.Map;
                pawn.rotationTracker.FaceTarget(victim);
                MoteMaker.ThrowText(victim.DrawPos, map, "Good night princess", Color.white);
                SoundDef voice = DefDatabase<SoundDef>.GetNamedSilentFail("SQP_GoodNightVoice");
                voice?.PlayOneShot(new TargetInfo(victim.Position, map));
                ExecutionUtility.DoHuntingExecution(pawn, victim);
            };
            yield return finish;
        }
    }

    /// <summary>
    /// Counts each colonist's time in combat (saved with the game), and starts a mercy-killing episode
    /// when a war-hardened colonist has someone nearby who is beyond saving.
    /// </summary>
    public class GameComponent_SquadPTSD : GameComponent
    {
        private Dictionary<string, int> combatTicks = new Dictionary<string, int>();
        private readonly Dictionary<int, int> cooldownUntil = new Dictionary<int, int>();

        public GameComponent_SquadPTSD(Game game)
        {
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref combatTicks, "combatTicks", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && combatTicks == null)
            {
                combatTicks = new Dictionary<string, int>();
            }
        }

        public int CombatTicks(Pawn p)
        {
            combatTicks.TryGetValue(p.thingIDNumber.ToString(), out int v);
            return v;
        }

        public void SetCombatTicks(Pawn p, int ticks)
        {
            combatTicks[p.thingIDNumber.ToString()] = ticks;
        }

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % Tuning.CheckInterval != 0)
            {
                return;
            }
            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                IReadOnlyList<Pawn> colonists = maps[m].mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < colonists.Count; i++)
                {
                    Pawn p = colonists[i];
                    if (p.RaceProps.Humanlike && SQP.InCombat(p))
                    {
                        string key = p.thingIDNumber.ToString();
                        combatTicks.TryGetValue(key, out int v);
                        combatTicks[key] = v + Tuning.CheckInterval;
                    }
                    TryStartEpisode(p, now);
                }
            }
        }

        public bool TryStartEpisode(Pawn p, int now)
        {
            if (!SQP.IsWarHardened(p) || p.Downed || p.InMentalState || !p.Awake() || p.Dead)
            {
                return false;
            }
            if (cooldownUntil.TryGetValue(p.thingIDNumber, out int until) && now < until)
            {
                return false;
            }
            // Anything holding their breakdowns off still holds this one off.
            //
            // The episode is started with forced: true, which is what makes it able to interrupt
            // whatever they were doing -- but forced also skips the game's own check for hediffs
            // that block mental breaks, and Squad's SQ_FrozenMinor / SQ_FrozenMajor are exactly
            // that. So a colonist Squad had just frozen under fire was being pulled into a mercy
            // killing anyway, with one mod overruling the other. Checked here by the same rule the
            // game uses rather than by name, so it holds for any mod's blocking hediff.
            List<Hediff> hediffs = p.health?.hediffSet?.hediffs;
            if (hediffs != null)
            {
                for (int i = 0; i < hediffs.Count; i++)
                {
                    if (hediffs[i].CurStage != null && hediffs[i].CurStage.blocksMentalBreaks)
                    {
                        return false;
                    }
                }
            }
            if (SQP.FindTarget(p) == null)
            {
                return false;
            }
            bool started = p.mindState.mentalStateHandler.TryStartMentalState(SQP_DefOf.SQP_MercyKilling, forced: true);
            if (started)
            {
                cooldownUntil[p.thingIDNumber] = now + Tuning.CooldownTicks;
            }
            return started;
        }
    }
}
