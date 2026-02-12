# Subscription Expiration Time Configuration Review

## Summary

This document reviews and confirms the subscription expiration time configuration across all related applications to ensure consistency.

---

## Configuration Analysis

### 1. MailSubscriptionFunctionApp (Subscription Creation)

**File**: `MailSubscriptionFunctionApp/Services/GraphSubscriptionService.cs` (lines 84-88)

**Configuration**:
```csharp
var expirationMinutes = Math.Min(
    _config.GetValue<int>("Graph:SubscriptionExpirationMinutes", 2880),
    4230 // Maximum for mail subscriptions
);
```

**Values**:
- **Default**: 2880 minutes = **48 hours = 2 days**
- **Maximum**: 4230 minutes (Microsoft Graph limit for mail subscriptions)
- **Configuration Key**: `Graph:SubscriptionExpirationMinutes`

**Fallback** (if Graph API returns null):
```csharp
SubscriptionExpirationTime = created.ExpirationDateTime?.DateTime ?? DateTime.UtcNow.AddHours(48)
```
- **Fallback**: 48 hours

**Current Configuration**:
- ❌ **NOT SET** in `local.settings.json` (uses default 2880 minutes)
- ❌ **NOT SET** in Kubernetes ConfigMap (uses default 2880 minutes)

---

### 2. MailSubscriptionRenewalFunctionApp (Subscription Renewal)

**File**: `MailSubscriptionRenewalFunctionApp/Functions/Subscriptions/RenewSubscriptionsFunction.cs` (line 107)

**Renewal Window**:
```csharp
var expiringSubs = await _repository.GetExpiringSubscriptionsAsync(
    TimeSpan.FromHours(5),  // Hardcoded: 5 hours before expiry
    cancellationToken);
```

**Renewal Duration**:
```csharp
_renewalDays = int.Parse(_configuration["GraphSubscription:RenewalDays"] ?? "2");
var newExpirationTime = DateTime.UtcNow.AddDays(_renewalDays);
```

**Values**:
- **Renewal Window**: **5 hours** (hardcoded - subscriptions expiring within 5 hours are renewed)
- **Renewal Duration**: **2 days** (default from `GraphSubscription:RenewalDays`)
- **Timer Schedule**: Every 6 hours (`0 0 */6 * * *`)

**Current Configuration**:
- ❌ `GraphSubscription:RenewalDays` **NOT SET** in Kubernetes ConfigMap (uses default 2 days)
- ✅ Renewal window is hardcoded to 5 hours (no configuration needed)

---

### 3. MailNotificationFunctionApp (Subscription Validation)

**File**: `MailNotificationFunctionApp/Services/NotificationHandler.cs` (lines 317-321)

**Current Query**:
```sql
SELECT clientstate  
FROM mailsubscriptions  
WHERE subscriptionid = @SubscriptionId  
AND subscriptionexpirationtime > NOW();
```

**Issue**: 
- ❌ **NO GRACE PERIOD** - Subscriptions are rejected immediately upon expiration
- ❌ No tolerance for Microsoft Graph processing delays
- ❌ No tolerance for clock skew between systems

---

## Configuration Summary

| Component | Setting | Value | Status |
|-----------|---------|-------|--------|
| **MailSubscriptionFunctionApp** | `Graph:SubscriptionExpirationMinutes` | 2880 (48 hours) | ✅ Default (not configured) |
| **MailSubscriptionRenewalFunctionApp** | `GraphSubscription:RenewalDays` | 2 days | ✅ Default (not configured) |
| **MailSubscriptionRenewalFunctionApp** | Renewal Window | 5 hours | ✅ Hardcoded |
| **MailNotificationFunctionApp** | Grace Period | **0 minutes** | ❌ **MISSING** |

---

## Problem Identified

### Root Cause

1. **Subscriptions expire after 48 hours**
2. **Renewal attempts to renew 5 hours before expiry** (at 43 hours)
3. **If renewal fails or is delayed**, subscriptions expire
4. **Validation immediately rejects expired subscriptions** with no grace period
5. **Microsoft Graph may send notifications slightly after expiration** due to:
   - Processing delays
   - Clock skew between systems
   - Network latency

### Impact

- Legitimate notifications are rejected with 401 Unauthorized
- Microsoft Graph may stop sending notifications for these subscriptions
- Data loss - notifications never reach the processing pipeline

---

## Recommended Solution

### Add Grace Period to Validation

**Rationale**: 
- Microsoft Graph documentation indicates notifications may arrive slightly after expiration
- Clock skew between systems can cause false expiration
- Processing delays can cause notifications to arrive after expiration

**Recommended Grace Period**: **5-10 minutes**

This provides:
- Tolerance for Graph API processing delays
- Tolerance for clock skew
- Safety margin without compromising security

### Implementation

**File**: `MailNotificationFunctionApp/Services/NotificationHandler.cs`

**Change Query**:
```sql
-- Current (no grace period)
WHERE subscriptionid = @SubscriptionId  
AND subscriptionexpirationtime > NOW();

-- Proposed (with 5-minute grace period)
WHERE subscriptionid = @SubscriptionId  
AND subscriptionexpirationtime > (NOW() - INTERVAL '5 minutes');
```

**Configuration**:
```csharp
// Add to configuration
SubscriptionValidation:ExpirationGracePeriodMinutes = 5
```

---

## Configuration Recommendations

### 1. Add to Kubernetes ConfigMap

```yaml
# Add to mailnotificationfunction-config ConfigMap
SubscriptionValidation__ExpirationGracePeriodMinutes: "5"
```

### 2. Add to local.settings.json (for local development)

```json
{
  "SubscriptionValidation:ExpirationGracePeriodMinutes": "5"
}
```

### 3. Optional: Make Grace Period Configurable

Create configuration class:
```csharp
public class SubscriptionValidationConfiguration
{
    public int ExpirationGracePeriodMinutes { get; set; } = 5;
    public bool AllowExpiredWithinGracePeriod { get; set; } = true;
}
```

---

## Verification Checklist

- ✅ **Subscription Creation**: 48 hours (2880 minutes) - CONFIRMED
- ✅ **Subscription Renewal**: 2 days - CONFIRMED  
- ✅ **Renewal Window**: 5 hours before expiry - CONFIRMED
- ✅ **Renewal Schedule**: Every 6 hours - CONFIRMED
- ❌ **Validation Grace Period**: **MISSING** - NEEDS TO BE ADDED

---

## Conclusion

The expiration time configuration is **consistent** across applications:
- Subscriptions are created with **48-hour expiration**
- Renewal attempts to renew **5 hours before expiry**
- Renewal extends subscriptions by **2 days**

**However**, the validation logic is **too strict** - it rejects subscriptions immediately upon expiration with no grace period. This causes legitimate notifications to be rejected.

**Action Required**: Add a **5-minute grace period** to the validation query to account for processing delays and clock skew.
