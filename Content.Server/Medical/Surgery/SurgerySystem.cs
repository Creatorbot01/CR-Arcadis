using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Server.Chat.Systems;
using Content.Server.Popups;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Medical.Surgery;
using Content.Shared.Medical.Surgery.Steps;
using Content.Shared.Medical.Surgery.Conditions;
using Content.Shared.Medical.Surgery.Effects.Step;
using Content.Shared.Medical.Surgery.Tools;
using Content.Shared.Mood;
using Content.Server.Atmos.Rotting;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Eye.Blinding.Systems;
//using Content.Shared.Medical.Wounds;
using Content.Shared.Prototypes;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using System.Linq;

namespace Content.Server.Medical.Surgery;

public sealed class SurgerySystem : SharedSurgerySystem
{
    [Dependency] private readonly BodySystem _body = default!;
    [Dependency] private readonly ChatSystem _chat = default!;

    [Dependency] private readonly DamageableSystem _damageableSystem = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly RottingSystem _rot = default!;
    [Dependency] private readonly BlindableSystem _blindableSystem = default!;
    //[Dependency] private readonly WoundsSystem _wounds = default!;

    private readonly List<EntProtoId> _surgeries = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SurgeryToolComponent, AfterInteractEvent>(OnToolAfterInteract);
        SubscribeLocalEvent<SurgeryTargetComponent, SurgeryStepDamageEvent>(OnSurgeryStepDamage);
        SubscribeLocalEvent<SurgeryDamageChangeEffectComponent, SurgeryStepEvent>(OnSurgeryDamageChange);
        SubscribeLocalEvent<SurgerySpecialDamageChangeEffectComponent, SurgeryStepEvent>(OnSurgerySpecialDamageChange);
        SubscribeLocalEvent<SurgeryStepAffixPartEffectComponent, SurgeryStepEvent>(OnStepAffixPartComplete);
        SubscribeLocalEvent<SurgeryStepEmoteEffectComponent, SurgeryStepEvent>(OnStepScreamComplete);
        SubscribeLocalEvent<SurgeryStepSpawnEffectComponent, SurgeryStepEvent>(OnStepSpawnComplete);

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        LoadPrototypes();
    }

    protected override void RefreshUI(EntityUid body)
    {
        var surgeries = new Dictionary<NetEntity, List<EntProtoId>>();
        foreach (var surgery in _surgeries)
        {
            if (GetSingleton(surgery) is not { } surgeryEnt)
                continue;

            foreach (var part in _body.GetBodyChildren(body))
            {
                var ev = new SurgeryValidEvent(body, part.Id);
                RaiseLocalEvent(surgeryEnt, ref ev);

                if (ev.Cancelled)
                    continue;

                surgeries.GetOrNew(GetNetEntity(part.Id)).Add(surgery);
            }

        }
        Logger.Debug($"Setting UI state with {surgeries}, {body} and {SurgeryUIKey.Key}");
        _ui.SetUiState(body, SurgeryUIKey.Key, new SurgeryBuiState(surgeries));
    }

    private void SetDamage(EntityUid body, DamageSpecifier damage, float partMultiplier,
        EntityUid user, EntityUid part)
    {
        var changed = _damageableSystem.TryChangeDamage(body, damage, true, origin: user, canSever: false, partMultiplier: partMultiplier);
        if (changed != null
            && changed.GetTotal() == 0
            && damage.GetTotal() < 0
            && TryComp<BodyPartComponent>(part, out var partComp))
        {
            var targetPart = _body.GetTargetBodyPart(partComp.PartType, partComp.Symmetry);
            _body.TryChangeIntegrity((part, partComp), damage.GetTotal().Float(), false, targetPart, out var _);
        }
    }

    private void OnToolAfterInteract(Entity<SurgeryToolComponent> ent, ref AfterInteractEvent args)
    {
        var user = args.User;
        if (args.Handled
            || !args.CanReach
            || args.Target == null
            || !TryComp<SurgeryTargetComponent>(args.User, out var surgery)
            || !surgery.CanOperate)
        {
            return;
        }
        /* lmao bet
        if (user == args.Target)
        {
            _popup.PopupEntity("You can't perform surgery on yourself!", user, user);
            return;
        }*/
        Logger.Debug("OnToolAfterInteract passed, opening UI");
        args.Handled = true;
        _ui.OpenUi(args.Target.Value, SurgeryUIKey.Key, user);
        Logger.Debug("UI opened");
        RefreshUI(args.Target.Value);
    }

    private void OnSurgeryStepDamage(Entity<SurgeryTargetComponent> ent, ref SurgeryStepDamageEvent args)
    {
        SetDamage(args.Body, args.Damage, args.PartMultiplier, args.User, args.Part);
    }

    private void OnSurgeryDamageChange(Entity<SurgeryDamageChangeEffectComponent> ent, ref SurgeryStepEvent args)
    {
        // This unintentionally punishes the user if they have an organ in another hand that is already used.
        // Imo surgery shouldnt let you automatically pick tools on both hands anyway, it should only use the one you've got in your selected hand.
        if (ent.Comp.IsConsumable)
        {
            if (args.Tools.Where(tool => TryComp<OrganComponent>(tool, out var organComp)
                && !_body.TrySetOrganUsed(tool, true, organComp)).Any())
                return;
        }

        var damageChange = ent.Comp.Damage;
        if (HasComp<ForcedSleepingComponent>(args.Body))
            damageChange = damageChange * ent.Comp.SleepModifier;

        SetDamage(args.Body, damageChange, 0.5f, args.User, args.Part);
    }

    private void OnSurgerySpecialDamageChange(Entity<SurgerySpecialDamageChangeEffectComponent> ent, ref SurgeryStepEvent args)
    {
        if (ent.Comp.IsConsumable)
        {
            if (args.Tools.Where(tool => TryComp<OrganComponent>(tool, out var organComp)
                && !_body.TrySetOrganUsed(tool, true, organComp)).Any())
                return;
        }

        if (ent.Comp.DamageType == "Rot")
        {
            _rot.ReduceAccumulator(args.Body, TimeSpan.FromSeconds(2147483648));
        }
        else if (ent.Comp.DamageType == "Eye")
        {
            if (TryComp(args.Body, out BlindableComponent? blindComp)
                && blindComp.EyeDamage > 0)
                _blindableSystem.AdjustEyeDamage((args.Body, blindComp), -blindComp!.EyeDamage);
        }
    }

    private void OnStepAffixPartComplete(Entity<SurgeryStepAffixPartEffectComponent> ent, ref SurgeryStepEvent args)
    {
        if (!TryComp(args.Surgery, out SurgeryPartRemovedConditionComponent? removedComp))
            return;

        var targetPart = _body.GetBodyChildrenOfType(args.Body, removedComp.Part, symmetry: removedComp.Symmetry).FirstOrDefault();

        if (targetPart != default)
        {
            var ev = new BodyPartEnableChangedEvent(true);
            RaiseLocalEvent(targetPart.Id, ref ev);
            // This is basically an equalizer, severing a part will badly damage it.
            // and affixing it will heal it a bit if its not too badly damaged.
            _body.TryChangeIntegrity(targetPart, targetPart.Component.Integrity - 20, false,
                _body.GetTargetBodyPart(targetPart.Component.PartType, targetPart.Component.Symmetry), out _);
        }

    }

    private void OnStepScreamComplete(Entity<SurgeryStepEmoteEffectComponent> ent, ref SurgeryStepEvent args)
    {
        if (!HasComp<ForcedSleepingComponent>(args.Body))
            _chat.TryEmoteWithChat(args.Body, ent.Comp.Emote);
    }
    private void OnStepSpawnComplete(Entity<SurgeryStepSpawnEffectComponent> ent, ref SurgeryStepEvent args)
    {
        if (TryComp(args.Body, out TransformComponent? xform))
            SpawnAtPosition(ent.Comp.Entity, xform.Coordinates);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<EntityPrototype>())
            LoadPrototypes();
    }

    private void LoadPrototypes()
    {
        _surgeries.Clear();
        foreach (var entity in _prototypes.EnumeratePrototypes<EntityPrototype>())
        {
            if (entity.HasComponent<SurgeryComponent>())
                _surgeries.Add(new EntProtoId(entity.ID));
        }
    }
}