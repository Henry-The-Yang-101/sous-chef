using System;
using System.Collections.Generic;

namespace SousChef.Plugin.Adapters
{
    public sealed class AdapterSpec
    {
        public string TypeName;
        public string Domain;
        public string[] Fields;
    }

    // Each field was selected from the installed build. No arbitrary getters,
    // coroutine enumeration, delegates, or network/account/save data are read.
    // Field names remain intact on the wire so units and provenance are auditable.
    public static class AdapterCatalog
    {
        public static readonly Dictionary<string, AdapterSpec> Specs = Create();

        private static Dictionary<string, AdapterSpec> Create()
        {
            var s = new Dictionary<string, AdapterSpec>();
            Add(s, "chef", "PlayerControls", "m_movement m_bRespawning m_bApplyGravity m_movementScale m_bServerControlled m_directlyUnderPlayerControl m_localVelocity m_xzSpeed m_interactionObjects m_activeControlsImpl m_currentPhysicsSurface m_timeBeforeFalling m_pickupDelay");
            Add(s, "chef", "PlayerIDProvider", "m_player m_teamID");
            Add(s, "chef", "PlayerControls+MovementData", "GravityStrength GravityStrengthSlopedGround RunSpeed TurnSpeed DashSpeed DashTime DashCooldown MaxSpeed StepHeightMax");
            Add(s, "chef", "PlayerControls+InteractionObjects", "m_iHandlePickup m_iHandleCatch m_iHandlePlacement m_interactable m_gridLocation m_TheOriginalHandlePickup");
            Add(s, "chef", "PlayerControlsImpl_Default", "m_serverImpl m_clientImpl");
            Add(s, "chef", "ClientPlayerControlsImpl_Default", "m_lastInteracted m_predictedInteracted m_sessionInteraction m_lastVelocity m_dashTimer m_aimingThrow m_movementInputSuppressed m_lastMoveInputDirection m_impactStartTime m_impactTimer m_impactVelocity m_timeOffGround m_isFalling m_lastPickupTimestamp");
            Add(s, "chef", "GroundCast", "m_landscapeMask m_radius m_offset m_groundCollider m_groundLayer m_groundPoint m_groundNormal m_groundDistance m_isCurrent");
            Add(s, "attachments", "ServerPlayerAttachmentCarrier", "m_carriedObjects");
            Add(s, "attachments", "ClientPlayerAttachmentCarrier", "m_carriedObjects");
            Add(s, "attachments", "PhysicalAttachment", "m_container m_motion m_surfaceMovable m_groundCast");
            Add(s, "attachments", "ServerPhysicalAttachment", "m_isHeld m_physicalAttachment m_bIsClientSidePredicted");
            Add(s, "attachments", "ClientPhysicalAttachment", "m_isHeld m_Parent m_bClientSidePredicted");
            Add(s, "attachments", "ServerAttachStation", "m_item m_pendingInit");
            Add(s, "attachments", "ClientAttachStation", "m_item");
            Add(s, "attachments", "ServerAttachStation+HolderAdapter", "m_attachStation");
            Add(s, "attachments", "ClientAttachStation+HolderAdapter", "m_attachStation");
            Add(s, "attachments", "AttachStation", "m_attachPoint m_pickupPriority m_placementPriority m_catchingPriority m_canCatch");

            Add(s, "food", "ServerIngredientContainer", "m_contents");
            Add(s, "food", "ClientIngredientContainer", "m_contents");
            Add(s, "food", "IngredientContainer", "m_capacity");
            Add(s, "food", "IngredientPropertiesComponent", "m_ingredientOrderNode");
            Add(s, "food", "ItemPropertiesComponent", "m_itemDefinition");
            Add(s, "food", "CookableIngredient", "m_ingredientOrderNode");
            Add(s, "food", "ServerCookableIngredient", "m_cookableIngredient m_cookingHandler");
            Add(s, "food", "ServerItemContainer", "m_ingredientContainer");
            Add(s, "food", "ServerLadleContainer", "m_ingredientContainer");
            Add(s, "food", "AssembledDefinitionNode", "m_freeObject");
            Add(s, "food", "IngredientAssembledNode", "m_ingriedientOrderNode");
            Add(s, "food", "ItemAssembledNode", "m_itemOrderNode");
            Add(s, "food", "CompositeAssembledNode", "m_composition m_optional");
            Add(s, "food", "CookedCompositeAssembledNode", "m_cookingStep m_progress m_recordedProgress");
            Add(s, "food", "MixedCompositeAssembledNode", "m_progress m_recordedProgress");
            Add(s, "food", "NullAssembledNode", "");
            Add(s, "food", "OrderDefinitionNode", "m_uID m_platingStep m_platingPrefab");
            Add(s, "food", "CompositeOrderNode", "m_composition m_optional");
            Add(s, "food", "CookedCompositeOrderNode", "m_cookingStep m_progress");
            Add(s, "food", "MixedCompositeOrderNode", "m_progress");
            Add(s, "food", "ItemOrderNode", "m_heatValue");
            Add(s, "food", "CookingStepData", "m_uID");
            Add(s, "food", "PlatingStepData", "m_uID");
            Add(s, "food", "PreparationContainer", "m_ingredientOrderNode");
            Add(s, "food", "ServerPreparationContainer", "m_itemContainer");
            Add(s, "food", "ServerCookableContainer", "m_itemContainer m_cookingHandler");
            Add(s, "food", "ServerMixableContainer", "m_MixingHandler m_IngredientContainer");
            Add(s, "food", "ServerPlate", "m_ingredientContainer");
            Add(s, "food", "Plate", "m_platingStep");
            Add(s, "food", "ServerStack", "m_stackItems");
            Add(s, "food", "ClientStack", "m_stackItems");
            Add(s, "food", "ServerPlateStackBase", "m_stack");
            Add(s, "food", "ClientPlateStackBase", "m_stack");
            Add(s, "food", "PlateStackBase", "m_platePrefab");

            Add(s, "processing", "ServerWorkableItem", "m_progress m_subProgress m_chopsPerSlice m_onWorkstation");
            Add(s, "processing", "ClientWorkableItem", "m_progress m_subProgress m_chopsPerSlice m_onWorkstation");
            Add(s, "processing", "ServerWorkstation", "m_item m_interacters");
            Add(s, "processing", "ClientWorkstation", "m_item m_interacters");
            Add(s, "processing", "ServerWorkstation+Interacter", "m_object m_actionLength m_actionTime m_actionTimer");
            Add(s, "processing", "ClientWorkstation+Interacter", "Component m_actionLength m_actionTime m_actionTimer");
            Add(s, "processing", "ClientCookingHandler", "m_cachedCookingState m_cachedCookingProgress m_isInCookingRegion");
            Add(s, "processing", "ClientMixingHandler", "m_cachedMixingState m_cachedMixingProgress");
            Add(s, "processing", "CookableProperties", "AllowedCookingSteps");
            Add(s, "interactions", "ServerInteractable", "m_interacters m_interactionSuppressed");
            Add(s, "interactions", "ClientInteractable", "m_interacters m_interactionSuppressed");
            Add(s, "chef", "ServerPlayerControlsImpl_Default", "m_lastInteracted m_autoSwitchTimer");
            Add(s, "processing", "WorkableItem", "m_stages m_nextPrefab");
            Add(s, "processing", "ServerCookingHandler", "m_ServerData");
            Add(s, "processing", "CookingStateMessage", "m_cookingState m_cookingProgress");
            Add(s, "processing", "CookingHandler", "m_cookingtime m_cookingType m_stationType");
            Add(s, "processing", "ServerMixingHandler", "m_serverData");
            Add(s, "processing", "MixingStateMessage", "m_mixingState m_mixingProgress");
            Add(s, "processing", "MixingHandler", "m_mixingTime m_mixingType");
            Add(s, "processing", "ServerCookingStation", "m_cookingSpeed m_isTurnedOn m_isCooking m_itemPot");
            Add(s, "processing", "CookingStation", "m_stationType m_attachRestrictions");
            Add(s, "processing", "ServerHeatedStation", "m_heatValue m_heatRange");
            Add(s, "processing", "HeatedStation", "m_dissipationRate m_coalOrderNode");
            Add(s, "dishes", "ServerWashingStation", "m_cleaningTimer m_plateCount m_interactor m_dryingAttachStation");
            Add(s, "dishes", "WashingStation", "m_cleanPlateTime m_dryingStation");
            Add(s, "dishes", "PlateReturnController", "m_plateReturnControllerDesc m_platesToReturn m_plateReturnStations");
            Add(s, "dishes", "PlateReturnController+PlateReturnControllerConfig", "m_plateReturnTime");
            Add(s, "dishes", "PlateReturnController+PlatesPendingReturn", "m_station m_timer m_platingStepData");
            Add(s, "dishes", "ServerPlateReturnStation", "m_isDirtyReturn m_stack m_attachStation");
            Add(s, "dishes", "ServerPlateStation", "m_data");
            Add(s, "dishes", "PlateStationMessage", "m_delivered m_success");
            Add(s, "spawners", "PickupItemSpawner", "m_itemPrefab m_pickupPriority m_spawnCost");
            Add(s, "spawners", "ServerPlacementItemSpawner", "m_orderDefinition m_attachStation");
            Add(s, "spawners", "ServerLimitedQuantityItem", "m_fLastActivity m_Manager");
            Add(s, "spawners", "ServerLimitedQuantityItemManager", "m_AllObjects");

            Add(s, "round", "ServerFlowControllerBase", "m_State m_inRound m_skipToEnd");
            Add(s, "round", "ClientFlowControllerBase", "m_inRound m_isFinished");
            Add(s, "round", "ServerKitchenFlowControllerBase", "m_roundTimer m_plateReturnController");
            Add(s, "round", "ClientKitchenFlowControllerBase", "m_roundTimer m_cachedRecipeList m_cachedCookingStepList");
            Add(s, "round", "ServerCampaignFlowController", "m_teamMonitor");
            Add(s, "round", "ClientCampaignFlowController", "m_teamMonitor");
            Add(s, "round", "ServerCompetitiveFlowController", "m_teamOneMonitor m_teamTwoMonitor");
            Add(s, "round", "ClientCompetitiveFlowController", "m_teamOneMonitor m_teamTwoMonitor");
            Add(s, "round", "ServerTeamMonitor", "m_score m_ordersController");
            Add(s, "round", "ClientTeamMonitor", "m_score m_ordersController m_players");
            Add(s, "round", "TeamMonitor+TeamScoreStats", "TotalBaseScore TotalTipsScore TotalMultiplier TotalCombo TotalTimeExpireDeductions ComboMaintained TotalSuccessfulDeliveries");
            Add(s, "orders", "ServerOrderControllerBase", "m_activeOrders m_timerUntilOrder m_autoProgress m_enableOrderExpiration");
            Add(s, "orders", "ClientOrderControllerBase", "m_activeOrders m_enableOrderExpiration");
            Add(s, "orders", "OrderController.ServerOrderData", "ID RecipeListEntry Lifetime Remaining");
            Add(s, "orders", "ClientOrderControllerBase+ActiveOrder", "ID RecipeListEntry");
            Add(s, "orders", "RecipeList+Entry", "m_order m_weight m_scoreForMeal");
            Add(s, "orders", "OrderController.OrderID", "m_id");
            Add(s, "round", "ServerRoundTimer", "m_timeLeft m_roundTimer m_timeLimit");
            Add(s, "round", "ClientRoundTimer", "m_timeLeft m_roundTimer m_timeLimit");
            Add(s, "round", "TimeManager", "m_paused m_timeMultiplier m_pauseMask m_frozenPhysics m_debugtimeScale");
            Add(s, "round", "TimeManager+FrozenPhysicsData", "m_layer m_frozenBody m_linearVelocity m_angularVelocity m_isKinematic m_useGravity");

            Add(s, "conveyors", "ServerConveyorStation", "m_conveying m_item m_responsibleForItem m_adjacentReceiver m_gridIndex m_data m_pendingAdjacentUpdate");
            Add(s, "conveyors", "ConveyorStationMessage", "m_receiverEntityID m_itemEntityID m_arriveTime");
            Add(s, "conveyors", "ConveyorStation", "m_conveySpeed m_conveyanceDirectionXZ");
            Add(s, "conveyors", "ServerTabletopConveyenceReceiver", "m_receiving m_itemJustRemoved m_item");
            Add(s, "surfaces", "Travelator", "m_speed m_directionXZ m_border m_gridManager m_gridIndex");
            Add(s, "surfaces", "SurfaceMovable", "m_surface m_prevSurface m_surfaceVelocity");
            Add(s, "surfaces", "PlayerPhysicsSurface", "Properties");
            Add(s, "surfaces", "PlayerPhysicsSurfaceProperties", "SpeedMultiplier Slippiness Slidiness UseOverrideGravityNormal OverrideGravityNormal GravityMultiplier");
            Add(s, "wind", "WindVolume", "m_windSpeed m_windFilter m_isWindy m_volumeBounds");
            Add(s, "wind", "WindAccumulator", "m_sources m_totalForce");
            Add(s, "transitions", "ServerDynamicFlowController", "m_data");
            Add(s, "transitions", "DynamicLevelMessage", "m_phase");
            Add(s, "transitions", "DynamicFlowController", "m_transitions");
            // Coroutine bodies are never traversed. Queue length is observed separately.
            Add(s, "transitions", "ClientDynamicFlowController", "m_phaseQueue");
            Add(s, "transitions", "AnimatedDynamicTransition", "m_worldAnimator m_worldAnimatorTrigger");
            Add(s, "triggers", "ServerTriggerTimer", "m_timer m_startedTiming m_triggeredAtStart");
            Add(s, "triggers", "TriggerTimer", "m_startTrigger m_completeTrigger m_time m_startTiming m_triggerAtStart");
            Add(s, "triggers", "TriggerAfterRandomTime", "m_timer MinValue MaxValue TriggerName");
            Add(s, "triggers", "TriggerAnimatorSetVariable", "m_triggerToReceive m_targetAnimator m_variableName m_variableType m_randomValue m_boolValue m_intValue m_minIntValue m_maxIntValue m_floatValue m_minFloatValue m_maxFloatValue");
            Add(s, "triggers", "ServerTriggerZone", "m_collidersOccupying");

            Add(s, "portals", "Teleportal", "m_teleportPoint m_exitPortal m_receiveDelay m_cooldownTime m_allowImmediateReteleport m_teleportArc");
            Add(s, "portals", "ServerTeleportal", "m_teleporting m_cooldown m_recentlyTeleported");
            Add(s, "cannons", "ServerCannon", "m_loadedObject m_flying m_readyToLaunch");
            Add(s, "cannons", "Cannon", "m_target m_attachPoint m_exitPoint m_button");
            Add(s, "piloting", "PilotMovement", "MoveSpeed SnapHalfLife m_previousPoseDifference m_velocityAverage");
            Add(s, "piloting", "PilotRotation", "m_transformToRotate m_minLimitDegrees m_maxLimitDegrees m_directionModifier");
            Add(s, "fire", "ServerFlammable", "m_fireStrength m_onFire m_canCatchFire m_fireEncouragedBy m_encourageSupressedTimer m_cooldownSupressedTimer");
            Add(s, "fire", "Flammable", "m_fireSpreadRadius m_overrideTargetFlammabilityTime m_overrideTargetFlammability m_startOnFire");
            Add(s, "fire", "ServerFireHazard", "m_destroyTimer");
            Add(s, "throwing", "ServerThrowableItem", "m_flightTimer m_flying m_thrower m_previousThrower m_throwerTimer");
            Add(s, "respawn", "RespawnCollider", "m_respawnType m_respawnFilter m_onlyRespawnables");
            Add(s, "respawn", "PlayerRespawnBehaviour", "m_respawnTime m_startLocation m_startParent");
            Add(s, "respawn", "ClientPlayerRespawnBehaviour", "m_isRespawning");
            Add(s, "respawn", "ServerUtensilRespawnBehaviour", "m_isRespawning m_IdealSpawnLocation");

            Add(s, "grid", "GridManager", "m_gridHalfSize m_gridOccupancy");
            Add(s, "grid", "QuadGridManager", "m_origin m_size");
            Add(s, "grid", "StaticGridLocation", "m_gridManager m_gridIndex");
            Add(s, "grid", "DynamicGridLocation", "m_occupying");
            Add(s, "grid", "GridIndex", "m_x m_y m_z");
            Add(s, "grid", "Point3", "X Y Z");

            Add(s, "horde", "GameModes.Horde.ServerHordeFlowController", "m_waveIndex m_waveTime m_plateReturnController m_targets m_enemies m_entries m_score");
            Add(s, "horde", "GameModes.Horde.ServerHordeEnemy", "m_recipeCount m_message m_target m_beginStateMachine m_attackTimer");
            Add(s, "horde", "GameModes.Horde.HordeEnemyMessage", "m_kind m_toState");
            Add(s, "horde", "GameModes.Horde.ServerHordeTarget", "m_interactors m_repairAmount <Health>k__BackingField");
            Add(s, "horde", "GameModes.Horde.ServerHordeLockable", "m_locked");
            Add(s, "horde", "GameModes.Horde.TeamScoreStats", "TotalHealth TotalMoneyEarned TotalMoneySpent TotalEnemiesDefeated");
            return s;
        }

        private static void Add(Dictionary<string, AdapterSpec> specs, string domain, string type, string fields)
        {
            specs.Add(type, new AdapterSpec { TypeName = type, Domain = domain,
                Fields = fields.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) });
        }
    }
}
