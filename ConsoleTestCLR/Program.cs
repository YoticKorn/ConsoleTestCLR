﻿using System.Formats.Asn1;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public unsafe class Program
{
    public static void Main()
    {
        var writeLineHook = MethodHook.Create((Action<string?>)Console.WriteLine);
        writeLineHook.AddHook((Delegate)HookedWriteLine);
        writeLineHook.AddHook((Delegate)Hooked2WriteLine);
        writeLineHook.Hook();

        /*
        var readLineHook = MethodHook.Create((Func<string?>)Console.ReadLine);
        readLineHook.AddHook((Delegate)HookedReadLine);
        readLineHook.AddHook((Delegate)Hooked2ReadLine);
        readLineHook.Hook();
        */

        Console.WriteLine("a");

        Console.ReadLine();
    }

    public static bool HookedWriteLine(ref string? text)
    {
        Console.WriteLine("Hello from 1-th hook!");

        if (text is null)
            return true;

        text += " hooked!";

        return true;
    }

    public static bool Hooked2WriteLine(ref string? text)
    {
        Console.WriteLine("Hello from 2-th hook!");

        if (text is null)
            return true;

        text = text.Substring(4);

        return true;
    }

    public static bool HookedReadLine(ref string? result)
    {
        if (DateTime.Now.Ticks % 3 == 0)
        {
            result = "bad data!";
            return false;
        }

        return true;
    }

    public static bool Hooked2ReadLine(ref string? result)
    {
        if (result is null)
            result = "hooked text!";

        return false;
    }
}

unsafe struct MethodSnapshoot
{
    public MethodSnapshoot(MethodInfo methodInfo) : this(clr_MethodDesc.ExtractFrom(methodInfo)) { }
    public MethodSnapshoot(clr_MethodDesc* methodDesc)
    {
        if (methodDesc is null)
            throw new ArgumentNullException($"[Korn.Hooking] MethodSnapshoot->.ctor(clr_MethodDesc*): The method descriptor is null");

        var data = methodDesc->GetPrecode()->AsFixupPrecode()->GetData();
        Target = &data->Target;
        TargetSnapshoot = data->Target;
    }

    public void** Target;
    public void* TargetSnapshoot;
}

unsafe class MethodHook
{
    static List<MethodHook> ActiveHooks = [];

    MethodHook(MethodInfoSummary targetMethod)
    {
        TargetMethod = targetMethod;

        RuntimeHelpers.PrepareMethod(TargetMethod.MethodHandle);
        TargetSnapshoot = new(targetMethod);
    }

    public readonly MethodSnapshoot TargetSnapshoot;
    public readonly MethodInfo TargetMethod;

    public MethodSnapshoot StubSnapshoot { get; private set; }
    public MethodInfo? StubMethod { get; private set; }

    public readonly List<MethodInfo> Hooks = [];
    public bool IsHooked { get; private set; }   

    public void AddHook(MethodInfoSummary hook)
    {
        var isHooked = IsHooked;
        if (isHooked)
            Unhook();

        Hooks.Add(hook);
        BuildStub();

        if (isHooked)
            Hook();
    }

    public void RemoveHook(MethodInfoSummary hook)
    {        
        var isRemoved = Hooks.Remove(hook);

        if (isRemoved)
        {
            var isHooked = IsHooked;
            if (isHooked)
                Unhook();

            BuildStub();

            if (isHooked)
                Hook();
        }
    }

    public void Hook()
    {
        if (IsHooked)
            return;
        IsHooked = true;

        *TargetSnapshoot.Target = StubSnapshoot.TargetSnapshoot;
    }

    public void Unhook()
    {
        if (!IsHooked)
            return;
        IsHooked = false;

        *TargetSnapshoot.Target = TargetSnapshoot.TargetSnapshoot;
    }

    void BuildStub()
    {
        StubMethod = MultiHookMethodGenerator.Generate(this, TargetMethod, Hooks);
        StubSnapshoot = new(StubMethod);
    }

    public static MethodHook Create(MethodInfoSummary targetMethod)
    {
        var existsHook = ActiveHooks.FirstOrDefault(hook => hook.TargetMethod == targetMethod.Method);
        if (existsHook is not null)
            return existsHook;
        return new(targetMethod);
    }
}

unsafe static class MultiHookMethodGenerator
{
    static ModuleBuilder? definedModule;
    static ModuleBuilder ResolveDynamicAssembly()
    {
        if (definedModule is null)
            definedModule = DefineDynamicAssembly(Guid.NewGuid().ToString());

        return definedModule;
    }

    static ModuleBuilder DefineDynamicAssembly(string name)
    {
        var assemblyName = new AssemblyName(name);
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);  
        var module = assembly.DefineDynamicModule(name);

        return module;
    }

    public static DynamicMethod Generate(MethodHook methodHook, MethodInfo target, List<MethodInfo> hooks)
    {
        var targetParameters = target.GetParameters().Select(param => param.ParameterType).ToArray();
        var moduleBuilder = ResolveDynamicAssembly();
        var typeBuilder = moduleBuilder.DefineType(Guid.NewGuid().ToString(), TypeAttributes.Public | TypeAttributes.AutoClass | TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit);
        var fieldBuilder = typeBuilder.DefineField(Guid.NewGuid().ToString(), typeof(nint), FieldAttributes.Public | FieldAttributes.Static);
        var type = typeBuilder.CreateType();
        var stubTargetField = type.GetRuntimeFields().First()!;

        var method = new DynamicMethod(
            name:              Guid.NewGuid().ToString(),
            attributes:        MethodAttributes.Public | MethodAttributes.Static,
            callingConvention: CallingConventions.Standard,
            returnType:        target.ReturnType,
            parameterTypes:    targetParameters,
            owner:             type,
            skipVisibility:    true
        );

        method.InitLocals = false;

        GenerateIL();

        Type delegateType;
        if (target.ReturnType == typeof(void))
            delegateType = Expression.GetActionType(targetParameters);
        else delegateType = Expression.GetFuncType([target.ReturnType, .. targetParameters]);
        method.CreateDelegate(delegateType); // force the CLR to compile this method
        
        var snapshoot = new MethodSnapshoot(method);
        stubTargetField.SetValue(null, (nint)snapshoot.TargetSnapshoot);

        return method;  

        void GenerateIL()
        {
            var il = method.GetILGenerator();

            var targetLocal = il.DeclareLocal(typeof(void).MakePointerType().MakePointerType());
            var returnLabel = il.DefineLabel();

            var targetParameters = target.GetParameters();
            long targetPointerAddress = (nint)methodHook.TargetSnapshoot.Target;
            long targetAddress = (nint)methodHook.TargetSnapshoot.TargetSnapshoot;

            if (target.ReturnType == typeof(void))
            {
                var hookCallCost = targetParameters.Length + 2 /* …, call, br.false */;
                var targetCallCost = targetParameters.Length + 1 /* …, call */;
                var prologueSize = 7;
                var bodySize = hookCallCost * hooks.Count + targetCallCost;
                var epilogueSize = 4;
                var methodSize = prologueSize + bodySize + epilogueSize;
                var epilogueLocation = prologueSize + bodySize - 1;

                /* prologue */
                il.Emit(OpCodes.Ldc_I8, targetPointerAddress);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I8, targetAddress);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);

                /* hooks calling */
                foreach (var hook in hooks)
                {
                    for (var argIndex = 0; argIndex < targetParameters.Length; argIndex++)
                        il.Emit(OpCodes.Ldarga_S, (byte)argIndex);

                    il.Emit(OpCodes.Call, hook);
                    il.Emit(OpCodes.Brfalse, returnLabel);
                }

                /* target method calling */                
                for (var argIndex = 0; argIndex < targetParameters.Length; argIndex++)
                {
                    if (targetParameters[argIndex].ParameterType.IsByRef)
                        il.Emit(OpCodes.Ldarga_S, (byte)argIndex);
                    else il.Emit(OpCodes.Ldarg_S, (byte)argIndex);
                }

                il.Emit(OpCodes.Call, target);

                il.MarkLabel(returnLabel);

                /* epilogue */
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldsfld, stubTargetField);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ret);
            }
            else
            {
                var resultLocal = il.DeclareLocal(target.ReturnType);
                var hookCallCost = targetParameters.Length + 3 /* …, ldloca, call, br.false */;
                var targetCallCost = targetParameters.Length + 1 /* …, call, stoloc */;
                var prologueSize = 7;
                var bodySize = hookCallCost * hooks.Count + targetCallCost;
                var epilogueSize = 5;
                var methodSize = prologueSize + bodySize + epilogueSize;
                var epilogueLocation = prologueSize + bodySize - 1;

                /* prologue */
                il.Emit(OpCodes.Ldc_I8, targetPointerAddress);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I8, targetAddress);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);

                /* hooks calling */
                foreach (var hook in hooks)
                {
                    for (var argIndex = 0; argIndex < targetParameters.Length; argIndex++)
                        il.Emit(OpCodes.Ldarga_S, (byte)argIndex);
                    il.Emit(OpCodes.Ldloca_S, 1);

                    il.Emit(OpCodes.Call, hook);
                    il.Emit(OpCodes.Brfalse, returnLabel);
                }

                /* target method calling */
                for (var argIndex = 0; argIndex < targetParameters.Length; argIndex++)
                {
                    if (targetParameters[argIndex].ParameterType.IsByRef)
                        il.Emit(OpCodes.Ldarga_S, (byte)argIndex);
                    else il.Emit(OpCodes.Ldarg_S, (byte)argIndex);
                }

                il.Emit(OpCodes.Call, target);
                il.Emit(OpCodes.Stloc_1);

                il.MarkLabel(returnLabel);

                /* epilogue */
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldsfld, stubTargetField);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ldloc_1);
                il.Emit(OpCodes.Ret);
            }
        }
    }
}

public record struct MethodInfoSummary(MethodInfo Method)
{
    public bool IsSignatureEquals(MethodInfoSummary equalsWith)
    {
        var a = Method;
        var b = equalsWith.Method;

        return IsEqualsAttributes() && IsEqualsReturnType() && IsEqualsArguments();

        bool IsEqualsAttributes() => a.IsStatic == b.IsStatic;
        bool IsEqualsReturnType() => a.ReturnType == b.ReturnType;
        bool IsEqualsArguments()
        {
            var aArgs = a.GetParameters();
            var bArgs = b.GetParameters();

            if (aArgs.Length != bArgs.Length)
                return false;

            var argsCount = aArgs.Length;
            for (var argIndex = 0; argIndex < argsCount; argIndex++)
                if (aArgs[argIndex].ParameterType != aArgs[argIndex].ParameterType)
                    return false;

            return true;
        }
    }

    public static implicit operator MethodInfo(MethodInfoSummary self) => self.Method;
    public static implicit operator MethodInfoSummary(MethodInfo method) => new(method);
    public static implicit operator MethodInfoSummary(Delegate method) => new(method.Method);
}

unsafe struct clr_LoaderAllocator { }

[StructLayout(LayoutKind.Sequential, Size = 0x10, Pack = 0)]
unsafe struct clr_MethodDesc
{
    public static class MethodDescFlags
    {
        public const ushort
            Classification = 0x0007,
            ClassificationCount = 0x0008,
            HasNonVtableSlot = 0x0008,
            MethodImpl = 0x0010,
            HasNativeCodeSlot = 0x0020,
            EnCAddedMethod = 0x0040,
            Static = 0x0080,
            ValueTypeParametersWalked = 0x0100,
            ValueTypeParametersLoaded = 0x0200,
            Duplicate = 0x0400,
            DoesNotHaveEquivalentValuetypeParameters = 0x0800,
            RequiresCovariantReturnTypeChecking = 0x1000,
            NotInline = 0x2000,
            fSynchronized = 0x4000,
            IsIntrinsic = 0x8000;
    }

    public static class MethodDescFlags3
    {
        public const ushort
            TokenRemainderMask = 0x0FFF,
            HasStableEntryPoint = 0x1000,
            HasPrecode = 0x2000,
            IsUnboxingStub = 0x4000,
            IsEligibleForTieredCompilation = 0x8000;
    }

    public static class MethodDescFlags4
    {
        public const byte
            ComputedRequiresStableEntryPoint = 0x01,
            RequiresStableEntryPoint = 0x02,
            TemporaryEntryPointAssigned = 0x04;
    }

    public static class MethodClassification
    {
        public const ushort
            IL = 0,
            FCall = 1,
            PInvoke = 2,
            EEimpl = 3,
            Array = 4,
            Instantiated = 5,
            ComInterop = 6,
            Dynamic = 7,
            Count = 8;
    }

    public static readonly int ALIGNMENT = sizeof(nint);

    public ushort Flags3;
    public byte ChunkIndex;
    public byte Flags4;
    public short SlotNumber;
    public short Flags;
    public clr_MethodDescCodeData* CodeData;

    public bool IsStatic => (Flags & MethodDescFlags.Static) != 0;
    public bool HasNonVtableSlot => (Flags & MethodDescFlags.HasNonVtableSlot) != 0;
    public bool MethodImpl => (Flags & MethodDescFlags.MethodImpl) != 0;
    public bool HasNativeCodeSlot => (Flags & MethodDescFlags.HasNativeCodeSlot) != 0;

    public bool HasStableEntryPoint => (Flags3 & MethodDescFlags3.HasStableEntryPoint) != 0;
    public bool HasPrecode => (Flags3 & MethodDescFlags3.HasPrecode) != 0;
    public bool IsUnboxingStub => (Flags3 & MethodDescFlags3.IsUnboxingStub) != 0;
    public bool IsWrapperStub => IsUnboxingStub || IsInstantiatingStub;
    public bool IsMethodImpl => (Flags & MethodDescFlags.MethodImpl) != 0;

    public int Classification => Flags & MethodDescFlags.Classification;
    public bool IsIL => Classification == MethodClassification.IL;
    public bool IsNoMetadata => Classification == MethodClassification.Dynamic;

    public bool HasNativeCode => GetNativeCode() is not null;

    public bool IsInstantiatingStub =>
        Classification == MethodClassification.Instantiated
        ? !IsUnboxingStub
        : AsInstantiatedMethodDesc()->IMD_IsWrapperStubWithInstantiations();

    public int GetBaseSize(int classification) => ClassificationSizes[classification];

    public int GetBaseSize() => GetBaseSize(Classification);

    public clr_MethodTable* GetMethodTable()
        => GetMethodDescChunk()->GetMethodTable();

    public clr_MethodDescChunk* GetMethodDescChunk()
    {   
        fixed (clr_MethodDesc* self = &this)
        {
            var val = (clr_MethodDescChunk*)((byte*)self - (sizeof(clr_MethodDescChunk) + ChunkIndex * ALIGNMENT));
            return val;
        }
    }

    public void** GetMethodNonVTableEntryPointPointer()
    {
        if (!HasNonVtableSlot)
            throw new Exception("it doesn't has vtable slot");

        var size = GetBaseSize();
        fixed (clr_MethodDesc* self = &this)
            return (void**)((byte*)self + size);
    }

    public void* GetMethodEntryPointIfExists()
    {
        if (HasNonVtableSlot)
        {
            var size = GetBaseSize();
            fixed (clr_MethodDesc* self = &this)
                return *(void**)((byte*)self + size);
        }

        var methodTable = GetMethodTable();
        if (methodTable is null)
            return null;

        return methodTable->GetSlot(SlotNumber);
    }

    public void SetEntryPoint(void* address) => *GetAddressOfSlot() = address;

    public void* GetTemporaryEntryPoint()
    {
        var entryPoint = GetMethodEntryPointIfExists();
        if (entryPoint is not null)
            return entryPoint;

        EnsureTemporaryEntryPoint();
        entryPoint = GetMethodEntryPointIfExists();

        return entryPoint;
    }

    public void EnsureTemporaryEntryPoint()
    {
        if (GetMethodEntryPointIfExists() is null)
            EnsureTemporaryEntryPointCore();
    }

    void EnsureTemporaryEntryPointCore()
    {
        if (GetTemporaryEntryPointIfExists() is null)
            EnsureTemporaryEntryPointCore(null);
    }

    void EnsureTemporaryEntryPointCore(clr_AllocMemTracker* pamTracker)
    {
        if (GetTemporaryEntryPointIfExists() is null)
        {
            GetMethodDescChunk()->DetermineAndSetIsEligibleForTieredCompilation();
        }        
    }

    public void* GetTemporaryEntryPointIfExists()
    {
        // var flags4 = Volatile.Load(); nah, I don't even want to implement that lame code.

        if ((Flags4 & MethodDescFlags4.TemporaryEntryPointAssigned) != 0)
            return CodeData->TemporaryEntryPoint;

        return null;
    }

    public bool DetermineIsEligibleForTieredCompilationInvariantForAllMethodsInChunk()
        => false; // so sweetty code, oooff - https://github.com/dotnet/dotnet/blob/df0660d28d5e8252a4f10823c41e76e9366c492a/src/runtime/src/coreclr/vm/method.cpp#L2825

    public void* GetNativeCode()
    {
        if (HasNativeCodeSlot)
            return *GetAddrOfNativeCodeSlot();

        if (!HasStableEntryPoint || HasPrecode)
            return null;

        return GetStableEntryPoint();
    }

    public void** GetAddressOfSlot()
    {
        if (HasNonVtableSlot)
        {
            var size = GetBaseSize();

            fixed (clr_MethodDesc* self = &this)
                return (void**)((byte*)self + size);
        }

        return GetMethodTable()->GetSlotPtr(SlotNumber);
    }

    public clr_LoaderAllocator* GetLoaderAllocator() => GetLoaderModule()->GetLoaderAllocator();

    public clr_Module* GetLoaderModule() => GetMethodDescChunk()->GetLoaderModule();

    public void Reset()
    {
        if (HasPrecode)
        {
            GetPrecode()->Reset();
        }
        else 
        {
            // InterlockedUpdateFlags3(enum_flag3_HasStableEntryPoint | enum_flag3_HasPrecode, FALSE);
            Flags3 |= MethodDescFlags3.HasStableEntryPoint | MethodDescFlags3.HasPrecode;

            *GetAddressOfSlot() = GetTemporaryEntryPoint();
        }

        if (HasNativeCodeSlot)
        {
            *GetAddrOfNativeCodeSlot() = null;
        }
    }

    public void** GetAddrOfNativeCodeSlot()
    {
        var size = ClassificationSizes[Flags & (MethodDescFlags.Classification | MethodDescFlags.HasNonVtableSlot | MethodDescFlags.MethodImpl)];

        fixed (clr_MethodDesc* self = &this)
            return (void**)((byte*)self + size);
    }

    public bool DetermineAndSetIsEligibleForTieredCompilation()
    {
        if (HasNativeCodeSlot && !IsWrapperStub && !IsJitOptimizationLevelRequested())
        {
            // InterlockedUpdateFlags3(clr_MethodDesc.MethodDescFlags3.IsEligibleForTieredCompilation, true);
            Flags3 |= MethodDescFlags3.IsEligibleForTieredCompilation;

            return true;
        }

        return false;
    }

    public clr_InstantiatedMethodDesc* AsInstantiatedMethodDesc()
    {
        fixed (clr_MethodDesc* self = &this)
            return (clr_InstantiatedMethodDesc*)self;
    }

    public bool IsJitOptimizationLevelRequested()
    {
        if (IsNoMetadata)
            return false;

        var attributes = GetImplAttributes();
        //return IsMiNoOptimization(attributes) || IsMiAggressiveOptimization(attributes);
        throw new NotImplementedException();
    }

    public int GetImplAttributes()
    {
        int props;
        //GetMDImport()->GetMethodImplProps(GetMemberDef(), null, &props);
        throw new NotImplementedException();
        return props;
    }

    public clr_mdMethodDef GetMemberDef()
    {
        var chunk = GetMethodDescChunk();
        var tokenRange = chunk->GetTokenRange();
        var tokenRemainder = (ushort)(Flags3 & MethodDescFlags3.TokenRemainderMask);
        return MergeToken(tokenRange, tokenRemainder);
    }

    const int METHOD_TOKEN_REMAINDER_BIT_COUNT = 12;
    public clr_mdToken MergeToken(ushort tokrange, ushort tokenRemainder) 
        => (clr_mdToken)((tokrange << METHOD_TOKEN_REMAINDER_BIT_COUNT) | tokenRemainder | clr_CoreHeader.TokenType.MethodDef);

    public clr_IMDInternalImport* GetMDImport() => GetModule()->GetMDImport();

    public clr_Module* GetModule() => GetMethodDescChunk()->GetMethodTable()->GetModule();

    public clr_Precode* GetPrecode() => GetPrecodeFromEntryPoint(GetStableEntryPoint());

    public clr_Precode* GetPrecodeFromEntryPoint(void* entryPoint)
    {
        // very complex logic with defines, may cause bugs due to inaccurate translation
        return (clr_Precode*)entryPoint;
    }

    public void* GetStableEntryPoint() => GetMethodEntryPointIfExists();

    public static clr_MethodDesc* ExtractFrom(MethodInfo op)
    {
        var a = *(nint*)(MethodInfo*)&op;
            
        if (op is DynamicMethod)
            return clr_Corelib_DynamicMethod.ExtractFrom(op)->MethodHandleInternal->Handle;
        else if (op.GetType().Name == "RuntimeMethodInfo")
            return clr_Corelib_RuntimeMethodInfo.ExtractFrom(op)->Handle;
        else throw new NotImplementedException();
    }

    public static readonly byte[] ClassificationSizes =
    [
       0x08, 0x10, 0x30, 0x18, 0x18, 0x20, 0x10, 0x28, 0x10, 0x18, 0x38, 0x20, 
       0x20, 0x28, 0x18, 0x30, 0x18, 0x20, 0x40, 0x28, 0x28, 0x30, 0x20, 0x38, 
       0x20, 0x28, 0x48, 0x30, 0x30, 0x38, 0x28, 0x40, 0x10, 0x18, 0x38, 0x20, 
       0x20, 0x28, 0x18, 0x30, 0x18, 0x20, 0x40, 0x28, 0x28, 0x30, 0x20, 0x38, 
       0x20, 0x28, 0x48, 0x30, 0x30, 0x38, 0x28, 0x40, 0x28, 0x30, 0x50, 0x38, 
       0x38, 0x40, 0x30, 0x48, 0x20, 0x28, 0x48, 0x30, 0x30, 0x38, 0x28, 0x40, 
       0x28, 0x30, 0x50, 0x38, 0x38, 0x40, 0x30, 0x48, 0x30, 0x38, 0x58, 0x40, 
       0x40, 0x48, 0x38, 0x50, 0x38, 0x40, 0x60, 0x48, 0x48, 0x50, 0x40, 0x58, 
       0x28, 0x30, 0x50, 0x38, 0x38, 0x40, 0x30, 0x48, 0x30, 0x38, 0x58, 0x40, 
       0x40, 0x48, 0x38, 0x50, 0x38, 0x40, 0x60, 0x48, 0x48, 0x50, 0x40, 0x58, 
       0x40, 0x48, 0x68, 0x50, 0x50, 0x58, 0x48, 0x60
    ];
}

struct clr_AllocMemTracker { }

struct clr_CoreHeader
{
    public static class TokenType
    {
        public const uint
            Module = 0x00000000,
            TypeRef = 0x01000000,
            TypeDef = 0x02000000,
            FieldDef = 0x04000000,
            MethodDef = 0x06000000,
            ParamDef = 0x08000000,
            InterfaceImpl = 0x09000000,
            MemberRef = 0x0a000000,
            CustomAttribute = 0x0c000000,
            Permission = 0x0e000000,
            Signature = 0x11000000,
            Event = 0x14000000,
            Property = 0x17000000,
            MethodImpl = 0x19000000,
            ModuleRef = 0x1a000000,
            TypeSpec = 0x1b000000,
            Assembly = 0x20000000,
            AssemblyRef = 0x23000000,
            File = 0x26000000,
            ExportedType = 0x27000000,
            ManifestResource = 0x28000000,
            NestedClass = 0x29000000,
            GenericParam = 0x2a000000,
            MethodSpec = 0x2b000000,
            GenericParamConstraint = 0x2c000000,
            String = 0x70000000,
            Name = 0x71000000,
            BaseType = 0x72000000;
    }
}

unsafe struct clr_mdMethodDef
{
    public clr_mdToken BaseMDToken;

    public static implicit operator uint(clr_mdMethodDef self) => *(uint*)&self;
    public static implicit operator clr_mdMethodDef(uint self) => *(clr_mdMethodDef*)&self;
    public static implicit operator clr_mdMethodDef(clr_mdToken self) => *(clr_mdMethodDef*)&self;
}

unsafe struct clr_mdToken
{
    public uint UIntBase;

    public static implicit operator uint(clr_mdToken self) => *(uint*)&self;
    public static implicit operator clr_mdToken(clr_mdMethodDef self) => *(clr_mdToken*)&self;
    public static implicit operator clr_mdToken(uint self) => *(clr_mdToken*)&self;
}

unsafe struct clr_InstantiatedMethodDesc
{
    public static class InstantiatedMethodDescFlags2
    {
        public const byte
            KindMask = 0x07,
            GenericMethodDefinition = 0x01,
            UnsharedMethodInstantiation = 0x02,
            SharedMethodInstantiation = 0x03,
            WrapperStubWithInstantiations = 0x04;
    }

    public clr_MethodDesc BaseMethodDesc;

    public bool IMD_IsWrapperStubWithInstantiations() 
        => (BaseMethodDesc.Flags & InstantiatedMethodDescFlags2.KindMask) == InstantiatedMethodDescFlags2.WrapperStubWithInstantiations;
}

unsafe struct clr_Precode 
{
    const int SIZEOF_PRECODE_BASE = 16;
    public fixed byte Data[SIZEOF_PRECODE_BASE];   

    public clr_StubPrecode* AsStubPrecode()
    {
        fixed (clr_Precode* self = &this)
            return (clr_StubPrecode*)self;
    }

    public clr_FixupPrecode* AsFixupPrecode()
    {
        fixed (clr_Precode* self = &this)
            return (clr_FixupPrecode*)self;
    }

    public new byte GetType() 
    {
        const int OFFSETOF_PRECODE_TYPE = 0;

        var type = Data[OFFSETOF_PRECODE_TYPE];

        if (type == clr_StubPrecode.Type)
            type = AsStubPrecode()->GetData()->Type;

        return type;
    }

    public clr_MethodDesc* GetMethodDesc()
    {
        var type = GetType();

        switch (type)
        {
            case clr_StubPrecode.Type:
                return AsStubPrecode()->GetMethodDesc();
            case clr_FixupPrecode.Type:
                return AsFixupPrecode()->GetMethodDesc();
            default: throw new NotImplementedException();
        }
    }

    public void Reset()
    {
        var methodDesc = GetMethodDesc();
        var type = GetType();

        if (type == clr_FixupPrecode.Type)
            fixed (clr_Precode* self = &this)
                Init(self, type, methodDesc, methodDesc->GetLoaderAllocator());
        else throw new NotImplementedException();
    }

    public void Init(clr_Precode* precode, byte type, clr_MethodDesc* methodDesc, clr_LoaderAllocator* loaderAllocator)
    {
        switch (type)
        {
            case clr_StubPrecode.Type:
                AsStubPrecode()->Init(precode->AsStubPrecode(), methodDesc, loaderAllocator);
                break;
            case clr_FixupPrecode.Type:
                AsFixupPrecode()->Init(precode->AsFixupPrecode(), methodDesc, loaderAllocator);
                break;
            default: throw new NotImplementedException();
        }
    }
}

unsafe struct clr_StubPrecode
{
    public const byte Type = 0x4C;
    public const int CodeSize = 24;

    public Data* GetData()
    {
        fixed (clr_StubPrecode* self = &this)
            return (Data*)((byte*)self + clr_LoaderHeap.GetStubCodePageSize());
    }

    public clr_MethodDesc* GetMethodDesc() => GetData()->GetMethodDesc();
    public void* GetTarget() => GetData()->GetTarget();
    public new int GetType() => GetData()->GetType();

    public void Init(clr_StubPrecode* precode, clr_MethodDesc* methodDesc, clr_LoaderAllocator* loaderAllocator, byte type = Type, void* target = null)
    {
        var data = precode->GetData();

        if (loaderAllocator is not null)
        {
            if (target is null)
                target = clr_Class.GetPreStubEntryPoint();
            data->Target = target;
        }

        data->MethodDesc = methodDesc;
        data->Type = type;
    }

    public unsafe struct Data
    {
        public clr_MethodDesc* MethodDesc;
        public void* Target;
        public byte Type;

        public clr_MethodDesc* GetMethodDesc() => MethodDesc;
        public void* GetTarget() => Target;
        public new int GetType() => Type;
    }
}

unsafe struct clr_InvalidPrecode
{
    public const byte Type = 0xCC;
}

unsafe struct clr_ThisPtrRetBufPrecode
{
    public const byte Type = 0x90;
}

unsafe struct clr_NDirectImportPrecode
{
    public const byte Type = 0x05;
}

unsafe struct clr_FixupPrecode
{
    public const byte Type = 0xFF;
    public const byte CodeSize = 24;
    public const byte FixupCodeOffset = 6;

    public Data* GetData()
    {
        fixed (clr_FixupPrecode* self = &this)
            return (Data*)((byte*)self + clr_LoaderHeap.GetStubCodePageSize());
    }

    public clr_MethodDesc* GetMethodDesc() => GetData()->GetMethodDesc();
    public void* GetTarget() => GetData()->GetTarget();

    public void Init(clr_FixupPrecode* precode, clr_MethodDesc* methodDesc, clr_LoaderAllocator* loaderAllocator)
    {
        var data = precode->GetData();

        data->MethodDesc = methodDesc;
        data->Target = (byte*)precode + FixupCodeOffset;
        data->PrecodeFixupThunk = clr_Class.GetPreStubEntryPoint();
    }

    public unsafe struct Data
    {
        public void* Target;
        public clr_MethodDesc* MethodDesc;
        public void* PrecodeFixupThunk;

        public clr_MethodDesc* GetMethodDesc() => MethodDesc;
        public void* GetTarget() => Target;
    }
}

unsafe struct clr_Class 
{
    static void* PreStub = (void*)(CoreClr.ModuleHandle + UnsafeAccessOffsets.Coreclr_PreStub);

    public static void* GetPreStubEntryPoint() => GetEEFuncEntryPoint();

    public static void* GetEEFuncEntryPoint() => PreStub;
}

unsafe struct Volatile
{
    public static T Load<T>(T* pointer) where T : unmanaged => *pointer;
}

unsafe struct StdMacroses
{
    public static bool IS_ALIGNED(void* address, int value) => IS_ALIGNED((nint)address, value);

    public static bool IS_ALIGNED(nint address, int value) => IS_ALIGNED((int)address, value);

    public static bool IS_ALIGNED(int address, int value) => (address & (value - 1)) == 0;
}

unsafe struct clr_LoaderHeap
{
    // https://github.com/dotnet/dotnet/blob/465b601267bab02a9353881312bfd7370d589d22/src/runtime/src/coreclr/utilcode/util.cpp#L1126
    public static uint GetOsPageSize() => 0x1000;

    // https://github.com/dotnet/dotnet/blob/465b601267bab02a9353881312bfd7370d589d22/src/runtime/src/coreclr/inc/loaderheap.h#L158
    public static uint GetStubCodePageSize() => Math.Max(16 * 1024, GetOsPageSize());
}

// noalign size = 0x14
[StructLayout(LayoutKind.Sequential, Size = 0x18, Pack = 0)]
unsafe struct clr_MethodDescChunk
{
    public static class MethodDescChunkFlags
    {
        public const ushort
            TokenRangeMask = 0x0FFF,
            DeterminedIsEligibleForTieredCompilation = 0x4000,
            LoaderModuleAttachedToChunk = 0x8000;
    }

    public clr_MethodTable* MethodTable;
    public clr_MethodDescChunk* Next;
    public byte Size;
    public byte Count;
    public ushort FlagsAndTokenRange;

    public clr_MethodTable* GetMethodTable() => MethodTable;

    public int SizeOf() => sizeof(clr_MethodDescChunk) + (Size + 1) * clr_MethodDesc.ALIGNMENT + (IsLoaderModuleAttachedToChunk() ? sizeof(clr_Module*) : 0);

    public clr_Module* GetLoaderModule()
    {
        if (IsLoaderModuleAttachedToChunk())
        {
            fixed (clr_MethodDescChunk* self = &this)
                return (clr_Module*)((byte*)self + SizeOf() - sizeof(clr_Module*));
        }
        else
        {
            return GetMethodTable()->GetLoaderModule();
        }
    }

    public ushort GetTokenRange() => (ushort)(FlagsAndTokenRange & MethodDescChunkFlags.TokenRangeMask);

    public bool IsLoaderModuleAttachedToChunk() => (FlagsAndTokenRange & MethodDescChunkFlags.LoaderModuleAttachedToChunk) != 0;

    public void DetermineAndSetIsEligibleForTieredCompilation()
    {
        if (!DeterminedIfMethodsAreEligibleForTieredCompilation())
        {
            var count = GetCount();
            var methodDesc = GetFirstMethodDesc();
            var chunkContainsEligibleMethods = methodDesc->DetermineIsEligibleForTieredCompilationInvariantForAllMethodsInChunk();
            /*
              not implemented because unreached
            */

            FlagsAndTokenRange |= MethodDescChunkFlags.DeterminedIsEligibleForTieredCompilation;
        }
    }

    public bool DeterminedIfMethodsAreEligibleForTieredCompilation()
        => (FlagsAndTokenRange & MethodDescChunkFlags.DeterminedIsEligibleForTieredCompilation) != 0;

    public byte GetCount() => (byte)(Count + 1);

    public clr_MethodDesc* GetFirstMethodDesc()
    {
        fixed (clr_MethodDescChunk* self = &this)
            return (clr_MethodDesc*)((byte*)self + sizeof(clr_MethodDescChunk));
    }
}

[StructLayout(LayoutKind.Sequential, Size = 0x28, Pack = 0)]
unsafe struct clr_MethodTable
{
    public int Flags;
    public int BaseSize;
    public int Flags2;
    public short NumVirtuals;
    public short NumInterfaces;
    public clr_MethodTable* ParentMethodTable;
    public clr_Module* Module;
    public clr_MethodTableAuxiliaryData* AuxiliaryData;

    public void* GetSlot(int slotNumber) => *GetSlotPtrRaw(slotNumber);

    public void** GetSlotPtr(int slotNumber) => GetSlotPtrRaw(slotNumber);

    public void** GetSlotPtrRaw(int slotNumber)
    {
        if (slotNumber < NumVirtuals)
            return *(GetVtableIndirections() + GetIndexOfVtableIndirection(slotNumber)) + GetIndexAfterVtableIndirection(slotNumber);
        else return GetNonVirtualSlotsArray(AuxiliaryData) - (1 + (slotNumber - NumVirtuals));
    }

    public void** GetNonVirtualSlotsArray(clr_MethodTableAuxiliaryData* auxiliaryData)
    {
        return (void**)((byte*)auxiliaryData + auxiliaryData->OffsetToNonVirtualSlots);
    }

    const int VTABLE_SLOTS_PER_CHUNK_LOG2 = 3;
    public int GetIndexOfVtableIndirection(int slotNumber) => slotNumber >> VTABLE_SLOTS_PER_CHUNK_LOG2;

    const int VTABLE_SLOTS_PER_CHUNK = 8;
    public int GetIndexAfterVtableIndirection(int slotNumber) => slotNumber & (VTABLE_SLOTS_PER_CHUNK - 1);

    public void*** GetVtableIndirections()
    {
        fixed (clr_MethodTable* self = &this)
            return (void***)((byte*)self + sizeof(clr_MethodTable));
    }

    public clr_Module* GetLoaderModule() => AuxiliaryData->GetLoaderModule();

    public clr_Module* GetModule() => Module;
}

[StructLayout(LayoutKind.Explicit, Size = 0x18, Pack = 0)]
unsafe struct clr_MethodTableAuxiliaryData
{
    [FieldOffset(0x00)]
    public int Flags;

    [FieldOffset(0x00)]
    public short LoFlags;

    [FieldOffset(0x02)]
    public short OffsetToNonVirtualSlots;

    [FieldOffset(0x08)]
    public clr_Module* LoaderModule;

    [FieldOffset(0x10)]
    public void* m_hExposedClassObject; // RUNTIMETYPEHANDLE

    public clr_Module* GetLoaderModule() => LoaderModule;
}

unsafe struct clr_MethodDescCodeData
{
    public clr_MethodDescVersioningState* VersioningState;
    public void* TemporaryEntryPoint;
}

// https://github.com/dotnet/dotnet/blob/df0660d28d5e8252a4f10823c41e76e9366c492a/src/runtime/src/coreclr/vm/codeversion.h#L470
unsafe struct clr_MethodDescVersioningState
{
    public clr_MethodDesc* MethodDesc;
    byte Flags;
    //…
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_Corelib_RuntimeMethodInfo
{
    [FieldOffset(0x50)]
    public clr_MethodDesc* Handle;

    public static clr_Corelib_RuntimeMethodInfo* ExtractFrom(MethodInfo op) => *(clr_Corelib_RuntimeMethodInfo**)&op;
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_Corelib_DynamicMethod
{
    [FieldOffset(0x10)]
    public clr_Corelib_RuntimeMethodStub* MethodHandleInternal;

    public static clr_Corelib_DynamicMethod* ExtractFrom(MethodInfo op) => *(clr_Corelib_DynamicMethod**)&op;
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_Corelib_RuntimeMethodStub
{
    [FieldOffset(0x50)]
    public clr_MethodDesc* Handle;
}

unsafe struct ArrayListInterator<T> where T : unmanaged
{
    public ArrayListInterator(clr_ArrayList<T> array) => this.array = array;

    clr_ArrayList<T> array;

    public List<nint> ToList()
    {
        List<nint> result = [];

        var count = array.Count;
        var firstBlock = array.FirstBlock;
        for (var i = 0; i < firstBlock.BlockSize; i++)
        {
            var element = firstBlock.GetArrayElement(i);
            result.Add((nint)element);
            if (result.Count == count)
                goto RETURN;
        }

        var next = firstBlock.Next;
        while (next is not null)
        {
            for (var i = 0; i < next->BlockSize; i++)
            {
                var element = next->GetArrayElement(i);
                result.Add((nint)element);
                if (result.Count == count)
                    goto RETURN;
            }

            next = next->Next;
        }

    RETURN:
        return result;
    }
}

static class CoreClr
{
    static nint moduleHandle;

    public static nint ModuleHandle
    {
        get
        {
            if (moduleHandle == 0)
            {
                moduleHandle = Interop.GetModuleHandle("coreclr");
                if (moduleHandle == 0)
                    throw new Exception("no loaded coreclr.dll module in the process");
            }

            return moduleHandle;
        }
    }
}

static class UnsafeAccessOffsets
{
    public readonly static nint
        Coreclr_AppDomain_m_pTheAppDomain = 0x488080,
        Coreclr_ClassLoader_LoadTypeDefOrRefThrowing = 0x27510,
        Coreclr_GetTypesInner = 0x96D14,
        Coreclr_PreStub = 0x15F050;
}

unsafe static class Interop
{
    const string kernel = "kernel32";

    [DllImport(kernel)]
    public static extern
        nint GetModuleHandle(string name);

    [DllImport(kernel)]
    public static extern
        nint VirtualAlloc(nint address, long size, MemoryState allocationType, MemoryProtect protect);

    [DllImport(kernel)]
    public static extern
        bool VirtualFree(nint address, long size, MemoryFreeType freeType);

    public static void CopyMemory(void* to, void* from, int byteLength) => Buffer.MemoryCopy(from, to, byteLength, byteLength);

    public static void WriteMemory(void* to, void* from, int len) => CopyMemory(to, from, len);

    public static void WriteMemory(void* str, byte[] array)
    {
        fixed (byte* pointer = array)
            WriteMemory(str, pointer, array.Length);
    }
}

unsafe struct MBI
{
    public nint BaseAddress;
    public nint AllocationBase;
    public uint AllocationProtect;
    public nint RegionSize;
    public int State;
    public int Protect;
    public int Type;
}

public enum MemoryFreeType
{
    Decommit = 0x4000,
    Release = 0x8000,
}

public enum MemoryState
{
    Commit = 0x1000,
    Free = 0x10000,
    Reserve = 0x2000
}

[Flags]
public enum MemoryProtect
{
    ZeroAccess = 0,
    NoAccess = 1,
    ReadOnly = 2,
    ReadWrite = 4,
    WriteCopy = 8,
    Execute = 16,
    ExecuteRead = 32,
    ExecuteReadWrite = 64,
    ExecuteWriteCopy = 128,
    Guard = 256,
    ReadWriteGuard = 260,
    NoCache = 512
}


enum ClassLoadLevel
{
    LoadBegin,
    LoadUnrestoredTypeKey,
    LoadUnrestored,
    LoadApproxParents,
    LoadExactParents,
    DependeciesLoaded,
    Loaded,
    LoadLevelFinal
}

[StructLayout(LayoutKind.Explicit, Size = 0x08)]
unsafe struct clr_TypeHandle
{
    [FieldOffset(0x00)]
    public nint Value;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
unsafe struct clr_System_RuntimeType
{
    [FieldOffset(0x18)]
    public clr_TypeHandle TypeHandle;
}

[StructLayout(LayoutKind.Sequential, Size = 0x18)]
unsafe struct clr_PtrArray<T> where T : unmanaged
{
    public clr_ArrayBase ArrayBase;

    nint x08;

    public T* Array;

    public List<nint> ToList()
    {
        var count = ArrayBase.Count;
        var result = new List<nint>(count);
        for (var i = 0; i < count; i++)
            result.Add((nint)(Array + i));

        return result;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 0x10)]
unsafe struct clr_ArrayBase
{
    [FieldOffset(0x00)]
    public clr_Object Object;

    [FieldOffset(0x08)]
    public int Count;
}

[StructLayout(LayoutKind.Explicit, Size = 0x08)]
unsafe struct clr_Object
{
    [FieldOffset(0x00)]
    public nint MethodTable;
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_AppDomain
{
    static clr_AppDomain** appDomainPointer = (clr_AppDomain**)(CoreClr.ModuleHandle + UnsafeAccessOffsets.Coreclr_AppDomain_m_pTheAppDomain);
    public static clr_AppDomain* AppDomain = *appDomainPointer;

    [FieldOffset(0x4B8)]
    public clr_DomainAssemblyList Assemblies;

    [FieldOffset(0x590)]
    public clr_Assembly* RootAssembly;
}

[StructLayout(LayoutKind.Explicit, Size = 0x58)]
unsafe struct clr_Assembly
{
    [FieldOffset(0x18)]
    public clr_Module* Module;

    [FieldOffset(0x30)]
    public bool IsDynamic;

    [FieldOffset(0x34)]
    public bool IsCollectible;

    [FieldOffset(0x54)]
    public bool IsInstrumentedStatus;
}

[StructLayout(LayoutKind.Explicit, Size = 0xA8)]
unsafe struct clr_ModuleBase
{
    [FieldOffset(0x00)]
    public vtable* VTable;

    [FieldOffset(0xC8)]
    public clr_Assembly* Assembly;

    [FieldOffset(0x98)]
    public clr_LoaderAllocator* LoaderAllocator;

    public clr_LoaderAllocator* GetLoaderAllocator() => LoaderAllocator;

    [StructLayout(LayoutKind.Explicit)]
    public struct vtable { }
}

[StructLayout(LayoutKind.Explicit, Size = 0x3B0)]
unsafe struct clr_Module
{
    const uint 
        MODULE_IS_TENURED           = 0x00000001,
        CLASSES_FREED               = 0x00000004,
        IS_EDIT_AND_CONTINUE        = 0x00000008,
        IS_PROFILER_NOTIFIED        = 0x00000010,
        IS_ETW_NOTIFIED             = 0x00000020,
        IS_REFLECTION_EMIT          = 0x00000040,
        DEBUGGER_USER_OVERRIDE_PRIV = 0x00000400,
        DEBUGGER_ALLOW_JIT_OPTS_PRIV= 0x00000800,
        DEBUGGER_TRACK_JIT_INFO_PRIV= 0x00001000,
        DEBUGGER_ENC_ENABLED_PRIV   = 0x00002000,
        DEBUGGER_PDBS_COPIED        = 0x00004000,
        DEBUGGER_IGNORE_PDBS        = 0x00008000,
        DEBUGGER_INFO_MASK_PRIV     = 0x0000Fc00,
        DEBUGGER_INFO_SHIFT_PRIV    = 10,
        IS_IJW_FIXED_UP             = 0x00080000,
        IS_BEING_UNLOADED           = 0x00100000;

    static void* GetTypesInnerDelegate = (void*)(CoreClr.ModuleHandle + UnsafeAccessOffsets.Coreclr_GetTypesInner);

    public clr_ModuleBase* AsModuleBase
    {
        get
        {
            fixed (clr_Module* pointer = &this)
                return (clr_ModuleBase*)pointer;
        }
    }

    [FieldOffset(0xA8)]
    public clr_Utf8String SimpleName;

    [FieldOffset(0xB0)]
    public clr_PEAssembly* PEAssembly;

    [FieldOffset(0xB8)]
    public uint TransientFlags;

    [FieldOffset(0x2E8)]
    public clr_DomainLocalModule* ModuleID;

    public bool IsReflectionEmit => (TransientFlags & IS_REFLECTION_EMIT) != 0;

    public clr_PtrArray<clr_System_RuntimeType>* GetManagedTypes()
    {
        fixed (clr_Module* self = &this)
            return ((delegate* unmanaged<clr_Module*, clr_PtrArray<clr_System_RuntimeType>*>)GetTypesInnerDelegate)(self);
    }

    public List<clr_TypeHandle> GetTypes()
    {
        var list = GetManagedTypes()->ToList();
        var result = new List<clr_TypeHandle>(list.Count);
        foreach (var item in list)
        {
            var type = (clr_System_RuntimeType*)item;
            result.Add(type->TypeHandle);
        }

        return result;
    }

    public clr_IMDInternalImport* GetMDImport()
    {
        if (IsReflectionEmit)
            return DacGetMDImport(GetReflectionModule(), true);

        return PEAssembly->GetMDImport();
    }

    public clr_IMDInternalImport* DacGetMDImport(clr_ReflectedModule* reflectionModule, bool throwEx)
        => throw new NotImplementedException("hah, it's to hard implement");

    public clr_ReflectedModule* GetReflectionModule()
    {
        fixed (clr_Module* self = &this)
            return (clr_ReflectedModule*)self;
    }

    public clr_LoaderAllocator* GetLoaderAllocator() => AsModuleBase->GetLoaderAllocator();
}

struct clr_ReflectedModule { }

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_DomainLocalModule
{
    [FieldOffset(0x00)]
    public clr_DomainAssembly* DomainAssembly;
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_PEAssembly
{
    [FieldOffset(0x18)]
    public clr_IMDInternalImport* MDImport;

    public clr_IMDInternalImport* GetMDImport() => MDImport; // not checked
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_IMDInternalImport
{
    [FieldOffset(0x00)]
    public vtable* VTable;

    [FieldOffset(0x10)]
    public clr_CLiteWeightStgdb<clr_CMiniMd> LiteWeightStgdb;

    public int EnumNext(clr_HENUMInternal* hEnum, int* token)
    {
        int current = hEnum->U.Current;
        if ((uint)current >= hEnum->U.End)
            return 0;

        if (hEnum->EnumType != 0)
        {
            hEnum->U.Current = current + 1;
            *token = *((int*)&hEnum->U4 + current);
        }
        else
        {
            *token = hEnum->Kind | current;
            ++hEnum->U.Current;
        }

        return 1;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct vtable
    {
        [FieldOffset(0x20)]
        public delegate* unmanaged<clr_IMDInternalImport*, clr_HENUMInternal*, clr_HResult> EnumTypeDefInit;
    }
}

unsafe struct clr_CLiteWeightStgdb<T> where T : unmanaged
{
    public clr_CMiniMd MiniMd;
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_CMiniMd
{
    [FieldOffset(0x08)]
    public clr_CMiniMdScheme Scheme;
}

[StructLayout(LayoutKind.Explicit, Size = 0xD0)]
unsafe struct clr_CMiniMdScheme
{
    [FieldOffset(0x18)]
    public fixed int Records[45];
}

enum clr_HResult : int { }

[StructLayout(LayoutKind.Explicit, Size = 0x48)]
unsafe struct clr_HENUMTypeDefInternalHolder
{
    [FieldOffset(0x00)]
    public clr_IMDInternalImport* InternalImport;

    [FieldOffset(0x08)]
    public clr_HENUMInternal Enum;

    [FieldOffset(0x40)]
    public int Acquired;
}

[StructLayout(LayoutKind.Explicit, Size = 0x38)]
unsafe struct clr_HENUMInternal
{
    [FieldOffset(0x00)]
    public int Kind;

    [FieldOffset(0x04)]
    public int Count;

    [FieldOffset(0x08)]
    public int EnumType;

    [FieldOffset(0x0C)]
    public Unnamed U;

    [FieldOffset(0x18)]
    public D6B39EC5995399DFFF9242F54E85023C U4;

    [StructLayout(LayoutKind.Explicit, Size = 0x0C)]
    public struct Unnamed
    {
        [FieldOffset(0x00)]
        public int Start;

        [FieldOffset(0x04)]
        public int End;

        [FieldOffset(0x08)]
        public int Current;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    public struct D6B39EC5995399DFFF9242F54E85023C
    {
        [FieldOffset(0x00)]
        public long AlignPad;
    }
}

unsafe struct clr_Utf8String
{
    public nint Address;

    public override string ToString() => ReadFromMemory(Address);

    public string ReadFromMemory(nint address) => new string((sbyte*)address);
}

[StructLayout(LayoutKind.Explicit)]
unsafe struct clr_DomainAssemblyList
{
    [FieldOffset(0x00)]
    public clr_ArrayList<clr_Assembly> Array;
}

unsafe struct clr_DomainAssembly { }

unsafe struct clr_ArrayList<T> where T : unmanaged
{
    public int Count;

    public clr_FirstArrayListBlock<T> FirstBlock;

    public List<nint> ToList() => new ArrayListInterator<T>(this).ToList();
}

[StructLayout(LayoutKind.Sequential, Pack = 0)]
unsafe struct clr_ArrayListBlock<T> where T : unmanaged
{
    public clr_ArrayListBlock<T>* Next;
    public int BlockSize;
    int padding;

    public T* GetArrayElement(int index)
    {
        fixed (clr_ArrayListBlock<T>* pointer = &this)
            return (T*)*((nint*)(pointer + 1) + index);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 0)]
unsafe struct clr_FirstArrayListBlock<T> where T : unmanaged
{
    const int ARRAY_BLOCK_SIZE_START = 5;

    public clr_ArrayListBlock<T>* Next;
    public int BlockSize;
    int padding;
    fixed long array[ARRAY_BLOCK_SIZE_START];

    public T* GetArrayElement(int index) => (T*)(nint)array[index];
}