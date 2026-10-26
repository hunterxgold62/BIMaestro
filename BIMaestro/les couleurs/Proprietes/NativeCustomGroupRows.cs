using System;
using System.Runtime.InteropServices;

namespace BIMaestro.PropertiesAppearance
{
    // Bind each installed native ABI to its own exact module images.
    internal static class NativeCustomGroupRows
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Insert(IntPtr core,uint row,uint count,IntPtr heights,IntPtr cells,IntPtr styles,uint columns,uint flags,int command);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Remove(IntPtr core,uint from,uint to,uint flags,int command);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddHeader(IntPtr element,ref int row,IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Construct(IntPtr text,IntPtr wide);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AddLegacyHeader(IntPtr element,ref int row,int groupId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DefaultConstruct(IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ConstructRange(IntPtr range,uint top,uint left,uint bottom,uint right);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTitle(IntPtr core,IntPtr range,IntPtr wide,int modify,int type,uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr text);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HeaderFlag(IntPtr element,uint row,uint column);
        private static Insert insert;private static Remove remove;private static AddHeader header;private static Construct construct;private static Destroy destroy;private static HeaderFlag flag;
        private static string qualificationError,qualifiedVersion;
        private static AddLegacyHeader legacyHeader;
        private static DefaultConstruct constructGroupId;
        private static ConstructRange constructRange;
        private static SetTitle setTitle;
        private static int nativeYear;
        internal static void Qualify(string version)
        {
            if(qualifiedVersion==version)return;
            if(qualificationError!=null)throw new NotSupportedException(qualificationError);
            try{QualifyModules(version);qualifiedVersion=version;}catch(Exception error){qualificationError=error.Message;throw;}
        }
        private static void QualifyModules(string version)
        {
            string[] hashes;
            switch(version)
            {
                case "2023": hashes=new[]{"A5EDF9739965772C0D34CF04A6E14B421444DBA474D09ABB96BEDAE1038575B2","487E6255847BA3ADF0A88544794B63DECB51DC3ACB1364B0412913C3FD70B2DD","D9FD5A2ECBC391FBE2D450273FFF905591360667624EBE760DC6554DDC0A2F82"};break;
                case "2024": hashes=new[]{"293A4C76DE0ED9D4CD9D7494FF9EBE82D026480AB2582D84857FE9C445AC870E","505F64F8124BFC41669A39ADAA53B5765DDA63D632150FC8C18A6FE5D35D6860","D8C8C0DEEE41C6D647758F8CD0B08BF0591C5DBE04CB3ED0F660A7F4A95D532E"};break;
                case "2025": hashes=new[]{"19F8A8BA3BD983C42897443EEC79203DEC8F47A31B67D63BD9B0C5BC5B7F7371","76FDA8B73A9E155F722FA70C14201D9F144687E11ED8EE0D629EA1DBA42C236E","E32587919DDC25B9ADF0FA978FC6A03400CE288355E08092858C869CE4D23E0A"};break;
                case "2026": hashes=new[]{"5BEE5228DEC41DEEB4B731F767F79B7529DD703B0D67332941D9BD1612A1EB3B","94ADF6E36F2AEA4BA0F897BB3910C1657CDB84D941AC87AAC6618ABB58491E4C","4A49AFE67D59782DCF4FCD04CCF6D483E2DC1BF1B32A67C75320C77A1DD5B88F"};break;
                case "2027": hashes=new[]{"5BEE5228DEC41DEEB4B731F767F79B7529DD703B0D67332941D9BD1612A1EB3B","BC865CA4C4804D835F3A38FE9F214DD01DE0D62240A93CDDBE8536FD0AF91450","37C94785E3EED5F1395D8BA2B0DBD8F847AE5D4CED94508DBEDA4C020FBCA626"};break;
                default:throw new NotSupportedException("Bandeaux personnalisés indisponibles pour Revit "+version+". Le profil reste enregistré.");
            }
            nativeYear=int.Parse(version,System.Globalization.CultureInfo.InvariantCulture);
            var og=PropertiesNativeModuleCache.Qualified("og1100asu.dll",hashes[0]).BaseAddress;
            var desktop=PropertiesNativeModuleCache.Qualified("DesktopMFC.dll",hashes[1]).BaseAddress;
            var utility=PropertiesNativeModuleCache.Qualified("Utility.dll",hashes[2]).BaseAddress;
            insert=PropertiesNativeModuleCache.Export<Insert>(og,"?InsertRows@CGXGridCore@@UEAAHKKPEAHPEAVCObArray@@1KIW4GXCmdType@@@Z");
            remove=PropertiesNativeModuleCache.Export<Remove>(og,"?RemoveRows@CGXGridCore@@UEAAHKKIW4GXCmdType@@@Z");
            if(nativeYear==2023)
                legacyHeader=PropertiesNativeModuleCache.Export<AddLegacyHeader>(desktop,"?addGroupHeader@ElemGrid@@QEAAXPEAHVElementId@@@Z");
            else
                // NotNull<int*> in 2027 is a pointer-sized wrapper, passed directly in RDX.
                header=PropertiesNativeModuleCache.Export<AddHeader>(desktop,nativeYear==2024
                    ?"?addGroupHeader@ElemGrid@@QEAAXPEAHVForgeTypeId@@@Z":nativeYear==2027
                    ?"?addGroupHeader@ElemGrid@@QEAAXV?$NotNull@PEAHX@@AEBVAString@@@Z"
                    :"?addGroupHeader@ElemGrid@@QEAAXPEAHAEBVAString@@@Z");
            if(nativeYear<2025)
            {
                constructRange=PropertiesNativeModuleCache.Export<ConstructRange>(og,"??0CGXRange@@QEAA@KKKK@Z");
                setTitle=PropertiesNativeModuleCache.Export<SetTitle>(og,"?SetValueRange@CGXGridCore@@QEAAHAEBVCGXRange@@PEB_WW4GXModifyType@@HI@Z");
                if(nativeYear==2024)
                {
                    constructGroupId=PropertiesNativeModuleCache.Export<DefaultConstruct>(utility,"??0ForgeTypeId@@QEAA@XZ");
                }
            }
            flag=PropertiesNativeModuleCache.Export<HeaderFlag>(desktop,"?isCellPartOfAGroupHeader@ElemGrid@@QEAAHKK@Z");
            construct=PropertiesNativeModuleCache.Export<Construct>(utility,"??0AString@@QEAA@PEB_W@Z");destroy=PropertiesNativeModuleCache.Export<Destroy>(utility,"??1AString@@QEAA@XZ");
        }
        private static void Ready(NativePropertiesGrid grid)
        {if(!grid.IsCurrent()||grid.IsEditing)throw new InvalidOperationException("Terminez la saisie dans Revit avant de modifier les bandeaux.");}
        internal static void Add(NativePropertiesGrid grid,string title)
        {
            Ready(grid);grid.ResetLayoutCell();uint before=grid.RowCount;
            if(insert(grid.Core,1,2,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0,0,0)==0)throw new InvalidOperationException("Insertion du bandeau refusée par Revit.");
            grid.RefreshRowCount();if(grid.RowCount!=before+2)throw new InvalidOperationException("Insertion native inattendue.");Ready(grid);
            IntPtr wide=Marshal.StringToHGlobalUni(title),text=Marshal.AllocHGlobal(IntPtr.Size);bool initialized=false;
            try
            {
                int row=1;
                if(nativeYear==2023)legacyHeader(grid.ElementGrid,ref row,-1);
                else if(nativeYear==2024)
                {
                    // ForgeTypeId is one AString slot. The by-value callee destroys its temporary.
                    constructGroupId(text);
                    header(grid.ElementGrid,ref row,text);
                }
                else{construct(text,wide);initialized=true;header(grid.ElementGrid,ref row,text);}
                if(row!=3||flag(grid.ElementGrid,2,1)==0)throw new InvalidOperationException("Le bandeau natif n’a pas été reconnu.");
                if(nativeYear<2025)
                {
                    // Older factories accept an empty group identity, not a label.
                    // Rename only the new header cell; never a model parameter or a borrowed style.
                    IntPtr range=Marshal.AllocHGlobal(24);
                    try{constructRange(range,2,1,2,1);if(setTitle(grid.Core,range,wide,0,0,0)==0)throw new InvalidOperationException("Le titre du bandeau a été refusé.");}
                    finally{Marshal.FreeHGlobal(range);}
                }
                if(grid.Value(2,1)!=title)throw new InvalidOperationException("Le titre natif n’a pas été reconnu.");
            }
            finally{if(initialized)destroy(text);Marshal.FreeHGlobal(text);Marshal.FreeHGlobal(wide);}
        }
        internal static void RemoveTail(NativePropertiesGrid grid,uint first)
        {
            Ready(grid);grid.ResetLayoutCell();uint count=grid.RowCount;
            if(first<1||first>count)throw new InvalidOperationException("Plage de bandeaux invalide.");
            if(remove(grid.Core,first,count,0,0)==0)throw new InvalidOperationException("Retrait du bandeau refusé par Revit.");
            grid.RefreshRowCount();if(grid.RowCount!=first-1)throw new InvalidOperationException("Retrait natif inattendu.");Ready(grid);
        }
    }
}
