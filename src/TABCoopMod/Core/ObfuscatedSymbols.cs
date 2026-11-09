namespace TABCoopMod.Core
{
    /// <summary>
    /// Centralized dictionary of Eazfuscator.NET obfuscated type, method, and field names
    /// in TheyAreBillions.exe (v1.0.14.29 x64).
    /// NEVER hardcode #=z... strings outside this class and GameBridge!
    /// See docs/03_REVERSE_ENGINEERING_MAP.md for full documentation.
    /// </summary>
    public static class ObfuscatedSymbols
    {
        // ====================================================================
        // 1. Top-Level Core Systems (Global Namespace)
        // ====================================================================
        public const string Type_ZXGame             = "#=z4RevDP3eECXqXS6JRA==";
        public const string Type_ZXSystem           = "#=zsW2J3r72Cu83";
        public const string Type_ZXSystemGameLevel  = "#=zxRcpu6e7NYzT7tGWqPjpOkc=";
        public const string Type_ZXSystemMainMenu   = "#=zLs$VX1eoAapVflmW_w==";
        public const string Type_ZXSystemCampaign   = "#=zUsJtyOzSfYDnqdeHjCSzszBbekLj";
        public const string Type_ZXSystemMapLayers  = "#=zJme8KFhmikprnkg_2CDeiQE=";
        /// <summary>Static singleton field holding the live ZXSystemMapLayers (fog config) instance.</summary>
        public const string Field_MapLayers_Current  = "#=zQFWYIvs=";        public const string Type_ZXSystemAudio      = "#=zJYbPa4gP2VI16zEh5y2Zxew=";
        public const string Type_ZXSystemBuilder    = "#=z5Ns_eH8ENLF7HFU5mg==";
        public const string Type_ZXSteamManager     = "#=z3Zxcp6RwVCZHa9xpeg==";
        public const string Type_ZXMessageBoxHelper = "#=zzi2cl4h1HYA5cX8aaw==";
        public const string Type_ZXFontCatalog      = "#=zqZKzsA47yWa6";

        // ====================================================================
        // 2. Universal Singleton Getter Name
        // ====================================================================
        /// <summary>
        /// Static singleton getter present on ZXGame, ZXSystemGameLevel, ZXSystemMainMenu, ZXGameState, ZXLevelState, etc.
        /// </summary>
        public const string Method_GetCurrentSingleton = "#=zuartwoo=";

        // ====================================================================
        // 3. ZXGame (#=z4RevDP3eECXqXS6JRA==) Members
        // ====================================================================
        public const string Field_ZXGame_AllCommandsList       = "#=zCj$vlp8=";
        public const string Method_ZXGame_Localize             = "#=zBYC_Wew=";
        public const string Method_ZXGame_GetSavesDirectory    = "#=zND5ul2zfzAnWdSnC0A==";
        public const string Method_ZXGame_WriteSaveFile        = "#=zMtGuEM2lBSlZ5BGWvg==";
        public const string Method_ZXGame_ReadSaveFile         = "#=z3YFOTVtBw_rT7oSQRA==";
        public const string Method_ZXGame_SaveGameFlow         = "#=zSV0_oCta8rEv";
        public const string Method_ZXGame_LoadGameAndEnter     = "#=z9IpK6P4YlW8GIY7olg==";
        public const string Method_ZXGame_ShowMayorSelection   = "#=zcITiOSD6YNmD";
        public const string Method_ZXGame_ShowSelectedMayors   = "#=zx3_1WsJs4Dyck79oqg==";
        public const string Method_ZXGame_ReturnToMainMenu     = "#=zqF2tlklT$8RE";

        // ====================================================================
        // 4. ZXSystemMainMenu (#=zLs$VX1eoAapVflmW_w==) Members
        // ====================================================================
        public const string Field_MainMenu_GridLayout          = "#=zUM4xl3ca4QS35TzMAg==";
        public const string Field_MainMenu_CategoryButtonsDict = "#=zyDj$brKr7icb";
        public const string Field_MainMenu_ReadyForInput       = "#=zBObIu5ds9WvT";
        public const string Method_MainMenu_SwitchCategory     = "#=zp8c19tQ=";
        public const string NestedEnum_MainMenu_TMenuCategory  = "TMenuCategory";

        // ====================================================================
        // 5. ZXSystemGameLevel (#=zxRcpu6e7NYzT7tGWqPjpOkc=) Members
        // ====================================================================
        public const string Field_LevelSys_PendingCommand      = "#=z0o_eM4o=";
        public const string Field_LevelSys_PendingTarget       = "#=zsP3wQ40=";
        public const string Field_LevelSys_MouseWorldPos       = "#=zetgfbFxpsSuV";
        public const string Field_LevelSys_MouseWorldCell      = "#=zMaiMxP6Lfc1s";
        public const string Field_LevelSys_MouseEntity         = "#=z_QMU7SQiIBfj";
        public const string Field_LevelSys_BuildAreaCursor     = "#=zZ$S0aaS3NQsgmtkOww==";
        public const string Field_LevelSys_CommandsManager     = "#=z4cHfUtNBoiM8";
        public const string Field_LevelSys_SelectionManager    = "#=zCJSdEwbGvSqa";

        public const string Method_LevelSys_InitLevel          = "#=zmTU4kueQctVr";
        public const string Method_LevelSys_OnSceneRightClick  = "#=zGxTz6vGn2dxDprCE7A==";
        public const string Method_LevelSys_OnSceneLeftClick   = "#=zX9V39sQYkQxr95dJ7A==";
        public const string Method_LevelSys_ExecutePendingCmd  = "#=zN0jTYBQMVgRf";
        public const string Method_LevelSys_GroupMoveSelected  = "#=zQp2M56bpPd9jmiNkVA==";
        public const string Method_LevelSys_AssignGroupMove    = "#=zgn6bDkSd9MGS3$WvAQ==";
        public const string Method_LevelSys_ShowGroundIndicator= "#=znoo7TyEtVemrA_J6FQ==";
        public const string Method_LevelSys_ShowTopBanner      = "#=zhWLC3K8=";
        public const string Method_LevelSys_EnqueueNotification= "#=zYWs2Gv9F3GD5";
        public const string Method_LevelSys_RefreshCmdPanel    = "#=zR1qHCYY=";

        // ====================================================================
        // 6. ZX.Commands.ZXCommand & Build & Train Members
        // ====================================================================
        public const string Method_ZXCommand_GetById           = "#=zqRskexw=";
        public const string Method_ZXCommand_GetByName         = "#=z4koKvss=";
        public const string Method_ZXCommand_GetGeneric        = "#=z5pCZsgI=";
        public const string Method_ZXCommand_Execute           = "#=zfmgzkuw=";
        public const string Method_ZXCommand_CanExecute        = "#=zKc8Kpc_EaFhf";
        public const string Method_ZXCommand_IsVisible         = "#=zXAX1VeKTLe7O";
        public const string Method_ZXCommand_GetResourcesCost  = "#=zcPu1$70ruiRE";
        public const string Method_ZXCommand_CancelExecution   = "#=z9whD4sw=";
        public const string Method_Build_PlaceBuilding         = "#=zBTwSMLo=";
        public const string Method_BuildOrTrain_GetForType     = "#=zQ4lx63I=";

        // ====================================================================
        // 7. ZX.Entities.ZXEntity & Components (CCommandable, CSelectable, CLife)
        // ====================================================================
        public const string Method_ZXEntity_GetCommandable     = "#=zkK_YqS85HrT3g75MJrlvMYo=";
        public const string Method_ZXEntity_GetSelectable      = "#=zTXZoQFXOaWH7";
        public const string Method_ZXEntity_GetLife            = "#=zS6TvFuwF_68l";
        public const string Method_ZXEntity_GetMovable         = "#=zCv3n3xlGOMilHuHlHA==";
        public const string Method_ZXEntity_GetDefaultParams   = "#=zcODCID3ur9P9";

        public const string Method_CCommandable_GetCurrentCmd  = "#=zE83e_h4=";
        public const string Method_CCommandable_SetCurrentCmd  = "#=zQKpiMFU=";
        public const string Method_CCommandable_EnqueueOrder   = "#=zRQYIK_o=";
        public const string Method_CCommandable_CancelQueueAt  = "#=z8g7GC7xLrr8j";
        public const string Method_CCommandable_CancelCurrent  = "#=zWGiN6nrTv8Dp";
        public const string Method_CCommandable_AdvanceQueue   = "#=zrz2EI1A=";

        public const string Field_CSelectable_SelectedList     = "#=zIiHCgn_odqH7";
        public const string Method_CSelectable_GetPrimary      = "#=zM3nhRXPdWdhx";
        public const string Method_CSelectable_UnselectAll     = "#=zClknEUY73pQc";

        // ====================================================================
        // 8. ZX.ZXLevelState Members
        // ====================================================================
        public const string Method_ZXLevelState_SaveTimeState  = "#=zQXHqcVh9mGZZ";
        public const string Method_ZXLevelState_RestoreTime    = "#=zF1YHTRUZSNRa";
        public const string Method_ZXLevelState_ConsumeRes     = "#=z5YtS5bwlM1kX";
        public const string Method_ZXLevelState_RefundRes      = "#=zG0slAM4=";
        public const string Method_ZXLevelState_CanAffordRes   = "#=zh4x6WKNtqKnr";
        public const string Method_ZXLevelState_GetDXLevel     = "#=zOHDY2QTzgRSB";

        // ====================================================================
        // 9. GUI & MessageBox Helpers
        // ====================================================================
        public const string Method_MsgBox_ShowAlert            = "#=zcvkFXLt$qEo6";
        public const string Method_MsgBox_ShowOkCancel         = "#=zy6vF6BSAUKyT";
        public const string Method_MsgBox_ShowYesNo            = "#=zFNDB6pz7Wqg3";
        public const string Method_MsgBox_ShowInputBox         = "#=znd_PcbSNuH79";
        public const string Method_MsgBox_ShowListSelector     = "#=zYjeCpah9Jpv2S1i8lQ==";
        public const string Method_MsgBox_GetPlainTextRenderer = "#=zth32r9N3TXbR";

        public const string Method_ZXWindow_AddBottomButton    = "#=zJMx0W0Q=";
        public const string Method_ZXWindow_ShowWindow         = "#=zyruJg$0=";
        public const string Method_ZXWindow_CloseWindow        = "#=zePbLN$s=";
        public const string Method_OMiniMap_PingCell           = "#=zvr$ku1Q=";
        public const string Method_SteamMgr_RunCallbacks       = "#=zVnIv8xS3EOZr";
    }
}
