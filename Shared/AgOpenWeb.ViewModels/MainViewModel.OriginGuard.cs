// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using AgOpenWeb.Models.State;
using Microsoft.Extensions.Logging;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// MainViewModel partial: handles the cycle worker's far-from-field warning.
/// The cycle detects when the live GPS position is well past any sane offset
/// from the loaded field's local-plane origin; this UI-thread handler drops
/// autosteer immediately and prompts the operator to close the field or keep
/// driving without guidance.
/// </summary>
public partial class MainViewModel
{
    private void HandleFarFromFieldWarning(FarFromFieldWarning warning)
    {
        if (IsAutoSteerEngaged)
        {
            IsAutoSteerEngaged = false;
            SyncGuidanceStateToPipeline();
        }

        double km = warning.DistanceMeters / 1000.0;
        // The web UI is the only UI: a host-side ShowConfirmationDialog here was never
        // answerable from the browser, so the operator saw nothing while
        // State.UI.ActiveDialog stayed parked on Confirmation (which makes HandleHotkey
        // bail until restart). The safety action — disengage — has already happened
        // above; the rest is information. Surface it as a hint (StatusMessage reaches
        // the tablet as a toast) and leave the field open: the operator can close it
        // from Field Operations if they really are at the wrong paddock.
        StatusMessage = $"GPS is {km:F1} km from the open field — autosteer disabled. " +
                        "Close the field if you're at a different paddock.";
        _logger.LogWarning("[OriginGuard] GPS {Km:F1} km from field origin — autosteer disengaged", km);
    }
}
