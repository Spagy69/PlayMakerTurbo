using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PlayMakerTurboInstaller
{
    // Patches PlayMaker.dll of My Winter Car. The patch only does surgery; all logic lives in PlayMakerTurbo.dll
    // (C#, compiled against the patched PlayMaker.dll), so every optimisation can be toggled in PlayMakerTurbo.ini.
    //  - PlayMakerFSM.Update/LateUpdate and PlayMakerFixedUpdate.FixedUpdate are renamed, so Unity stops calling
    //    them per component; PlayMakerTurbo.FsmTicker ticks them from one managed loop.
    //  - Fsm.UpdateStateChanges resets loop counters only when some state was entered since the last reset.
    //  - Fsm.GameObject, Fsm.Active, FsmState.OnUpdate, ActionHelpers.DoMousePick and FsmProperty.Get/SetValue
    //    forward to PlayMakerTurbo.Core.
    //  - Fsm.UpdateDelayedEvents returns early when there are no delayed events.
    //  - Members the ticker reads are made public; instance fields also get NotSerialized so Unity's serialization
    //    (and Instantiate copies) stay exactly as before. PlayMaker's own ActionData serializer reads public
    //    fields of actions, so FsmStateAction and FsmProperty get properties instead of fields.
    // Always patches from PlayMaker.dll.orig, so running it twice is safe.
    internal static class PatchEngine
    {
        public const string TurboAssembly = "PlayMakerTurbo";
        private const MethodAttributes Accessor = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName;
        private static readonly Version TurboVersion = new Version(1, 0, 0, 0);

        public static bool IsPatched(string playMakerDll)
        {
            using (ModuleDefinition module = ModuleDefinition.ReadModule(playMakerDll))
                return module.AssemblyReferences.Any(r => r.Name == TurboAssembly);
        }

        public static void Patch(string managed, Action<string> log)
        {
            string dll = Path.Combine(managed, "PlayMaker.dll");
            string orig = dll + ".orig";
            if (!File.Exists(dll))
                throw new FileNotFoundException(dll + " not found.");

            if (!File.Exists(orig))
            {
                if (IsPatched(dll))
                    throw new InvalidOperationException("PlayMaker.dll is already patched but the PlayMaker.dll.orig backup is gone. Verify the game files in Steam and run this again.");
                File.Copy(dll, orig);
                log("Backup created: PlayMaker.dll.orig");
            }
            else if (!IsPatched(dll) && !File.ReadAllBytes(dll).SequenceEqual(File.ReadAllBytes(orig)))
            {
                // The game (or Steam verify) replaced PlayMaker.dll with a different build, the old backup is stale.
                File.Copy(dll, orig, true);
                log("PlayMaker.dll changed since the last patch, backup refreshed.");
            }

            DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managed);

            string tmp = dll + ".tmp";
            using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(orig, new ReaderParameters { AssemblyResolver = resolver }))
            {
                Apply(asm.MainModule);
                asm.Write(tmp);
            }

            File.Copy(tmp, dll, true);
            File.Delete(tmp);
            log("PlayMaker.dll patched.");
        }

        public static void Restore(string managed, Action<string> log)
        {
            string dll = Path.Combine(managed, "PlayMaker.dll");
            string orig = dll + ".orig";
            if (!File.Exists(orig))
                throw new FileNotFoundException("No PlayMaker.dll.orig backup, nothing to restore.");

            File.Copy(orig, dll, true);
            log("Original PlayMaker.dll restored.");

            string turbo = Path.Combine(managed, TurboAssembly + ".dll");
            if (File.Exists(turbo))
            {
                File.Delete(turbo);
                log("PlayMakerTurbo.dll deleted.");
            }
        }

        private static void Apply(ModuleDefinition module)
        {
            AssemblyNameReference turbo = new AssemblyNameReference(TurboAssembly, TurboVersion);
            module.AssemblyReferences.Add(turbo);

            TypeDefinition playMakerFsm = GetType(module, "PlayMakerFSM");
            TypeDefinition proxyBase = GetType(module, "PlayMakerProxyBase");
            TypeDefinition fixedProxy = GetType(module, "PlayMakerFixedUpdate");
            TypeDefinition fsm = GetType(module, "HutongGames.PlayMaker.Fsm");
            TypeDefinition fsmState = GetType(module, "HutongGames.PlayMaker.FsmState");
            TypeDefinition helpers = GetType(module, "HutongGames.PlayMaker.ActionHelpers");
            TypeReference ticker = new TypeReference(TurboAssembly, "FsmTicker", module, turbo);
            TypeReference core = new TypeReference(TurboAssembly, "Core", module, turbo);

            // Ticker routing
            RenameToPublic(playMakerFsm, "Update", "TurboUpdate");
            RenameToPublic(playMakerFsm, "LateUpdate", "TurboLateUpdate");
            RenameToPublic(fixedProxy, "FixedUpdate", "TurboFixedUpdate");
            // Wake points: the ticker queues an FSM only when something can give it work (see FsmTicker).
            playMakerFsm.Fields.Add(new FieldDefinition("turboEntry", FieldAttributes.Public | FieldAttributes.NotSerialized, module.TypeSystem.Object));
            PrependThisCall(GetMethod(playMakerFsm, "OnEnable"), StaticCall(ticker, "Enabled", module.TypeSystem.Void, playMakerFsm));
            PrependThisCall(GetMethod(playMakerFsm, "OnDisable"), StaticCall(ticker, "Disabled", module.TypeSystem.Void, playMakerFsm));
            PrependThisCall(GetMethod(fsm, "Start"), StaticCall(ticker, "Wake", module.TypeSystem.Void, fsm));
            PrependThisCall(GetMethod(fsm, "DelayedEvent", 2), StaticCall(ticker, "Wake", module.TypeSystem.Void, fsm));
            PrependThisCall(GetMethod(fsm, "DelayedEvent", 3), StaticCall(ticker, "Wake", module.TypeSystem.Void, fsm));
            PrependThisCall(GetMethod(fsmState, "ActivateActions", 1), StaticCall(ticker, "WakeState", module.TypeSystem.Void, fsmState));
            proxyBase.Attributes = (proxyBase.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public;
            fixedProxy.Attributes = (fixedProxy.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.Public;
            MakePublic(GetField(proxyBase, "playMakerFSMs"));
            AddForwardingMessage(module, fixedProxy, "OnEnable", StaticCall(ticker, "RegisterFixed", module.TypeSystem.Void, fixedProxy));
            AddForwardingMessage(module, fixedProxy, "OnDisable", StaticCall(ticker, "UnregisterFixed", module.TypeSystem.Void, fixedProxy));

            // Members read by the ticker
            MakePublic(GetField(fsm, "activeStateEntered"));
            MakePublic(GetField(fsm, "switchToState"));
            MakePublic(GetField(fsm, "delayedEvents"));
            MakePublic(GetField(fsmState, "finished"));
            MakePublic(GetField(fsmState, "fsm")); // the Fsm getter logs an error when it is null
            MakePublic(GetField(playMakerFsm, "fsm")); // [SerializeField], stays serialized; the getter also rewrites Owner
            MakePublic(GetMethod(fsmState, "CheckAllActionsFinished"));
            MakePublic(GetMethod(fsmState, "set_StateTime", 1));
            MakePublic(GetField(helpers, "mousePickRaycastTime"));
            MakePublic(GetField(helpers, "mousePickDistanceUsed"));
            MakePublic(GetField(helpers, "mousePickLayerMaskUsed"));

            // Loop counter reset only when needed
            FieldDefinition dirty = new FieldDefinition("turboLoopDirty", FieldAttributes.Assembly | FieldAttributes.NotSerialized, module.TypeSystem.Boolean);
            fsm.Fields.Add(dirty);
            MarkDirtyOnEnter(fsmState, dirty);
            RewriteUpdateStateChanges(module, fsm, fsmState, dirty);

            // GameObject cache storage
            TypeReference monoBehaviour = GetMethod(fsm, "get_Owner").ReturnType;
            TypeReference gameObject = GetMethod(fsm, "get_GameObject").ReturnType;
            fsm.Fields.Add(new FieldDefinition("turboOwner", FieldAttributes.Public | FieldAttributes.NotSerialized, monoBehaviour));
            fsm.Fields.Add(new FieldDefinition("turboGameObject", FieldAttributes.Public | FieldAttributes.NotSerialized, gameObject));

            // Per-action cache of which callbacks the action's type overrides.
            TypeDefinition action = GetType(module, "HutongGames.PlayMaker.FsmStateAction");
            AddCachedProperty(module, action, "TurboFlags", module.TypeSystem.Int32);

            // FsmProperty (Get/SetProperty actions): the original Get/SetValue are kept under new names and new
            // Get/SetValue forward to Core, which uses cached typed delegates instead of boxing reflection when
            // the result is provably the same and falls back to the originals otherwise.
            TypeDefinition fsmProperty = GetType(module, "HutongGames.PlayMaker.FsmProperty");
            AddCachedProperty(module, fsmProperty, "TurboAccessor", module.TypeSystem.Object);
            AddFieldGetter(fsmProperty, "TurboTarget", GetField(fsmProperty, "targetObjectCached"));
            AddFieldGetter(fsmProperty, "TurboMembers", GetField(fsmProperty, "memberInfo"));
            ReplaceKeepingOriginal(fsmProperty, "GetValue", "TurboOriginalGetValue", StaticCall(core, "PropertyGet", module.TypeSystem.Void, fsmProperty));
            ReplaceKeepingOriginal(fsmProperty, "SetValue", "TurboOriginalSetValue", StaticCall(core, "PropertySet", module.TypeSystem.Void, fsmProperty));

            // Forwarders into PlayMakerTurbo.Core
            Forward(GetMethod(fsm, "get_GameObject"), StaticCall(core, "FsmGameObject", gameObject, fsm));
            Forward(GetMethod(fsm, "get_Active"), StaticCall(core, "FsmActive", module.TypeSystem.Boolean, fsm));
            Forward(GetMethod(fsmState, "OnUpdate"), StaticCall(core, "StateOnUpdate", module.TypeSystem.Void, fsmState));
            Forward(GetMethod(helpers, "DoMousePick", 2), StaticCall(core, "DoMousePick", module.TypeSystem.Void, module.TypeSystem.Single, module.TypeSystem.Int32));

            // Event routing to the FSMs of one GameObject, see EventRouting. Originals kept for the switch.
            TypeReference routing = new TypeReference(TurboAssembly, "EventRouting", module, turbo);
            TypeDefinition fsmEvent = GetType(module, "HutongGames.PlayMaker.FsmEvent");
            TypeDefinition fsmEventData = GetType(module, "HutongGames.PlayMaker.FsmEventData");
            MakePublic(GetMethod(fsm, "GetEventDataSentByInfo"));
            ReplaceWithArgs(fsm, GetMethod(fsm, "BroadcastEventToGameObject", "GameObject", "FsmEvent", "FsmEventData", "Boolean", "Boolean"),
                "TurboOriginalBroadcastEventToGameObject", StaticCall(routing, "BroadcastToGameObject", module.TypeSystem.Void, fsm, gameObject, fsmEvent, fsmEventData, module.TypeSystem.Boolean, module.TypeSystem.Boolean));
            ReplaceWithArgs(fsm, GetMethod(fsm, "SendEventToFsmOnGameObject", "GameObject", "String", "FsmEvent"),
                "TurboOriginalSendEventToFsmOnGameObject", StaticCall(routing, "SendToFsmOnGameObject", module.TypeSystem.Void, fsm, gameObject, module.TypeSystem.String, fsmEvent));
            ReplaceWithArgs(fsm, GetMethod(fsm, "BroadcastEvent", "FsmEvent", "Boolean"),
                "TurboOriginalBroadcastEvent", StaticCall(routing, "Broadcast", module.TypeSystem.Void, fsm, fsmEvent, module.TypeSystem.Boolean));

            // if (Core.DelayedEventsEarlyOut && delayedEvents.Count == 0) return;
            MethodDefinition updateDelayed = GetMethod(fsm, "UpdateDelayedEvents");
            FieldDefinition delayedEvents = GetField(fsm, "delayedEvents");
            FieldReference earlyOutFlag = new FieldReference("DelayedEventsEarlyOut", module.TypeSystem.Boolean, core);
            // Built from the field's own List<DelayedEvent> type so it binds to the game's mscorlib, not the patcher's runtime.
            MethodReference listCount = new MethodReference("get_Count", module.TypeSystem.Int32, delayedEvents.FieldType) { HasThis = true };
            Instruction original = updateDelayed.Body.Instructions[0];
            Prepend(updateDelayed, il => new[]
            {
                il.Create(OpCodes.Ldsfld, earlyOutFlag),
                il.Create(OpCodes.Brfalse, original),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldfld, delayedEvents),
                il.Create(OpCodes.Callvirt, listCount),
                il.Create(OpCodes.Brtrue, original),
                il.Create(OpCodes.Ret),
            });
        }

        private static TypeDefinition GetType(ModuleDefinition module, string fullName)
        {
            TypeDefinition type = module.GetType(fullName);
            if (type == null)
                throw new InvalidOperationException("Type not found: " + fullName);
            return type;
        }

        private static MethodDefinition GetMethod(TypeDefinition type, string name, int paramCount = 0)
        {
            MethodDefinition method = type.Methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == paramCount);
            if (method == null)
                throw new InvalidOperationException("Method not found: " + type.Name + "." + name);
            return method;
        }

        // An overload picked by its parameter type names.
        private static MethodDefinition GetMethod(TypeDefinition type, string name, params string[] parameterTypes)
        {
            MethodDefinition method = type.Methods.FirstOrDefault(m => m.Name == name
                && m.Parameters.Select(p => p.ParameterType.Name).SequenceEqual(parameterTypes));
            if (method == null)
                throw new InvalidOperationException("Method not found: " + type.Name + "." + name + "(" + string.Join(", ", parameterTypes) + ")");
            return method;
        }

        private static FieldDefinition GetField(TypeDefinition type, string name)
        {
            FieldDefinition field = type.Fields.FirstOrDefault(f => f.Name == name);
            if (field == null)
                throw new InvalidOperationException("Field not found: " + type.Name + "." + name);
            return field;
        }

        // Built by hand so the patcher does not need PlayMakerTurbo.dll, which is itself compiled against the patched PlayMaker.dll.
        private static MethodReference StaticCall(TypeReference owner, string name, TypeReference returnType, params TypeReference[] parameters)
        {
            MethodReference method = new MethodReference(name, returnType, owner) { HasThis = false };
            foreach (TypeReference p in parameters)
                method.Parameters.Add(new ParameterDefinition(p));
            return method;
        }

        private static void RenameToPublic(TypeDefinition type, string name, string newName)
        {
            MethodDefinition method = GetMethod(type, name);
            method.Name = newName;
            MakePublic(method);
        }

        private static void MakePublic(IMemberDefinition member)
        {
            FieldDefinition field = member as FieldDefinition;
            if (field != null)
            {
                bool serialized = field.CustomAttributes.Any(a => a.AttributeType.Name == "SerializeField");
                field.Attributes = (field.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Public;
                if (!field.IsStatic && !serialized)
                    field.Attributes |= FieldAttributes.NotSerialized;
                return;
            }

            MethodDefinition method = member as MethodDefinition;
            if (method != null)
                method.Attributes = (method.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Public;
        }

        private static void Prepend(MethodDefinition method, Func<ILProcessor, Instruction[]> build)
        {
            ILProcessor il = method.Body.GetILProcessor();
            Instruction first = method.Body.Instructions[0];
            foreach (Instruction instruction in build(il))
                il.InsertBefore(first, instruction);
        }

        // Target(this); at the top of an instance method.
        private static void PrependThisCall(MethodDefinition method, MethodReference target)
        {
            Prepend(method, il => new[] { il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, target) });
        }

        // Replaces the body with: return Target(this or args...);
        private static void Forward(MethodDefinition method, MethodReference target)
        {
            MethodBody body = method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            ILProcessor il = body.GetILProcessor();
            if (method.HasThis)
                il.Append(il.Create(OpCodes.Ldarg_0));
            foreach (ParameterDefinition p in method.Parameters)
                il.Append(il.Create(OpCodes.Ldarg, p));
            il.Append(il.Create(OpCodes.Call, target));
            il.Append(il.Create(OpCodes.Ret));
        }

        private static void AddForwardingMessage(ModuleDefinition module, TypeDefinition type, string name, MethodReference target)
        {
            MethodDefinition method = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Void);
            ILProcessor il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, target));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(method);
        }

        // Private NotSerialized backing field + public get/set property.
        private static void AddCachedProperty(ModuleDefinition module, TypeDefinition type, string name, TypeReference propertyType)
        {
            FieldDefinition field = new FieldDefinition("_" + name, FieldAttributes.Private | FieldAttributes.NotSerialized, propertyType);
            type.Fields.Add(field);
            AddFieldGetter(type, name, field);

            MethodDefinition setter = new MethodDefinition("set_" + name, Accessor, module.TypeSystem.Void);
            setter.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, propertyType));
            ILProcessor il = setter.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, field));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(setter);
            type.Properties.First(p => p.Name == name).SetMethod = setter;
        }

        // Read-only public property over an existing (private) field.
        private static MethodDefinition AddFieldGetter(TypeDefinition type, string name, FieldDefinition field)
        {
            MethodDefinition getter = new MethodDefinition("get_" + name, Accessor, field.FieldType);
            ILProcessor il = getter.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, field));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(getter);
            type.Properties.Add(new PropertyDefinition(name, PropertyAttributes.None, field.FieldType) { GetMethod = getter });
            return getter;
        }

        // Renames the original method (made public) and adds a new method with the old name and signature
        // that calls target(this). Existing callers bind by name and signature, so they reach the new method.
        private static void ReplaceKeepingOriginal(TypeDefinition type, string name, string originalName, MethodReference target)
        {
            MethodDefinition original = GetMethod(type, name);
            MethodDefinition replacement = new MethodDefinition(name, original.Attributes, original.ReturnType);
            original.Name = originalName;
            MakePublic(original);

            ILProcessor il = replacement.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, target));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(replacement);
            RedirectCalls(type.Module, original, replacement);
        }

        // Like ReplaceKeepingOriginal, for an instance method with parameters: the new method keeps the name,
        // signature and parameter defaults and calls target(this, args...).
        private static void ReplaceWithArgs(TypeDefinition type, MethodDefinition original, string originalName, MethodReference target)
        {
            MethodDefinition replacement = new MethodDefinition(original.Name, original.Attributes, original.ReturnType);
            foreach (ParameterDefinition p in original.Parameters)
            {
                ParameterDefinition copy = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
                if (p.HasConstant)
                    copy.Constant = p.Constant;
                replacement.Parameters.Add(copy);
            }
            original.Name = originalName;
            MakePublic(original);

            ILProcessor il = replacement.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Ldarg_0));
            foreach (ParameterDefinition p in replacement.Parameters)
                il.Append(il.Create(OpCodes.Ldarg, p));
            il.Append(il.Create(OpCodes.Call, target));
            il.Append(il.Create(OpCodes.Ret));
            type.Methods.Add(replacement);
            RedirectCalls(type.Module, original, replacement);
        }

        // Callers in other assemblies bind by name and reach the replacement on their own, but inside this module
        // a call holds the MethodDefinition itself, which now is the renamed original. Point those calls (including
        // the original's calls to itself) at the replacement, so they take the same path as outside callers.
        private static void RedirectCalls(ModuleDefinition module, MethodDefinition original, MethodDefinition replacement)
        {
            foreach (TypeDefinition type in AllTypes(module.Types))
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (method == replacement || !method.HasBody)
                        continue;
                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand == original)
                            instruction.Operand = replacement;
                    }
                }
            }
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types)
            {
                yield return type;
                foreach (TypeDefinition nested in AllTypes(type.NestedTypes))
                    yield return nested;
            }
        }

        private static void MarkDirtyOnEnter(TypeDefinition fsmState, FieldDefinition dirty)
        {
            // if (fsm != null) fsm.turboLoopDirty = true;   -- OnEnter is the only place loopCount grows.
            FieldDefinition fsmField = GetField(fsmState, "fsm");
            MethodDefinition onEnter = GetMethod(fsmState, "OnEnter");
            Instruction first = onEnter.Body.Instructions[0];
            Prepend(onEnter, il => new[]
            {
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldfld, fsmField),
                il.Create(OpCodes.Brfalse, first),
                il.Create(OpCodes.Ldarg_0),
                il.Create(OpCodes.Ldfld, fsmField),
                il.Create(OpCodes.Ldc_I4_1),
                il.Create(OpCodes.Stfld, dirty),
            });
        }

        private static void RewriteUpdateStateChanges(ModuleDefinition module, TypeDefinition fsm, TypeDefinition fsmState, FieldDefinition dirty)
        {
            // while (IsSwitchingState && !HitBreakpoint) SwitchState(switchToState);
            // if (turboLoopDirty) { turboLoopDirty = false; foreach (FsmState s in States) s.ResetLoopCount(); }
            MethodDefinition method = GetMethod(fsm, "UpdateStateChanges");
            MethodDefinition isSwitching = GetMethod(fsm, "get_IsSwitchingState");
            MethodDefinition hitBreakpoint = GetMethod(fsm, "get_HitBreakpoint");
            MethodDefinition switchState = GetMethod(fsm, "SwitchState", 1);
            MethodDefinition getStates = GetMethod(fsm, "get_States");
            MethodDefinition resetLoopCount = GetMethod(fsmState, "ResetLoopCount");
            FieldDefinition switchToState = GetField(fsm, "switchToState");

            MethodBody body = method.Body;
            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            VariableDefinition states = new VariableDefinition(new ArrayType(fsmState));
            VariableDefinition i = new VariableDefinition(module.TypeSystem.Int32);
            body.Variables.Add(states);
            body.Variables.Add(i);
            body.InitLocals = true;

            ILProcessor il = body.GetILProcessor();
            Instruction loopCheck = il.Create(OpCodes.Ldarg_0);
            Instruction afterLoop = il.Create(OpCodes.Ldarg_0);
            Instruction forBody = il.Create(OpCodes.Ldloc, states);
            Instruction forCond = il.Create(OpCodes.Ldloc, i);
            Instruction ret = il.Create(OpCodes.Ret);

            il.Append(loopCheck);
            il.Append(il.Create(OpCodes.Call, isSwitching));
            il.Append(il.Create(OpCodes.Brfalse, afterLoop));
            il.Append(il.Create(OpCodes.Call, hitBreakpoint));
            il.Append(il.Create(OpCodes.Brtrue, afterLoop));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldfld, switchToState));
            il.Append(il.Create(OpCodes.Call, switchState));
            il.Append(il.Create(OpCodes.Br, loopCheck));

            il.Append(afterLoop);
            il.Append(il.Create(OpCodes.Ldfld, dirty));
            il.Append(il.Create(OpCodes.Brfalse, ret));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stfld, dirty));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, getStates));
            il.Append(il.Create(OpCodes.Stloc, states));
            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stloc, i));
            il.Append(il.Create(OpCodes.Br, forCond));

            il.Append(forBody);
            il.Append(il.Create(OpCodes.Ldloc, i));
            il.Append(il.Create(OpCodes.Ldelem_Ref));
            il.Append(il.Create(OpCodes.Callvirt, resetLoopCount));
            il.Append(il.Create(OpCodes.Ldloc, i));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stloc, i));

            il.Append(forCond);
            il.Append(il.Create(OpCodes.Ldloc, states));
            il.Append(il.Create(OpCodes.Ldlen));
            il.Append(il.Create(OpCodes.Conv_I4));
            il.Append(il.Create(OpCodes.Blt, forBody));

            il.Append(ret);
        }
    }
}
