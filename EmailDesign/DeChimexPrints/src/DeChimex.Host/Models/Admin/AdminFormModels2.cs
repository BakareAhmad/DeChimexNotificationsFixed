using System.ComponentModel.DataAnnotations;

namespace DeChimex.Host.Models.Admin;

/// <summary>Form models for the remaining admin MVC pages (inventory, suppliers,
/// staff users, procurement, settings).</summary>

public sealed class SupplierFormViewModel
{
    public Guid Id { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200, ErrorMessage = "Name is too long.")]
    public string Name { get; set; } = string.Empty;

    public string? Contact { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Category { get; set; }

    [StringLength(30)]
    public string? Status { get; set; } = "Active";
}

public sealed class StaffUserFormViewModel
{
    [Required(ErrorMessage = "First name is required.")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required.")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    // Mirrors the Username domain type (case-insensitive here: the domain
    // normalises to lower-case). Command validation remains authoritative.
    [Required(ErrorMessage = "Username is required.")]
    [MinLength(3, ErrorMessage = "Username must be between 3 and 30 characters.")]
    [MaxLength(30, ErrorMessage = "Username must be between 3 and 30 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9]+[a-zA-Z0-9._-]*[a-zA-Z0-9]+$",
        ErrorMessage = "Username may only contain letters, digits, dots, underscores and hyphens, and must start and end with a letter or digit.")]
    public string Username { get; set; } = string.Empty;

    public List<Guid> RoleIds { get; set; } = new();
}

/// <summary>Change a staff member's username (shown to customers in chat).</summary>
public sealed class UsernameFormViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? CurrentUsername { get; set; }

    [Required(ErrorMessage = "Username is required.")]
    [MinLength(3, ErrorMessage = "Username must be between 3 and 30 characters.")]
    [MaxLength(30, ErrorMessage = "Username must be between 3 and 30 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9]+[a-zA-Z0-9._-]*[a-zA-Z0-9]+$",
        ErrorMessage = "Username may only contain letters, digits, dots, underscores and hyphens, and must start and end with a letter or digit.")]
    public string Username { get; set; } = string.Empty;
}

public sealed class StockInFormViewModel
{
    [Range(1, int.MaxValue, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }
}

public sealed class StockAdjustFormViewModel
{
    [Range(-1000000, 1000000, ErrorMessage = "Adjustment out of range.")]
    public int Delta { get; set; }

    [Required(ErrorMessage = "Reason is required.")]
    public string Reason { get; set; } = string.Empty;
}

public sealed class ProcurementCreateItemViewModel
{
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal? UnitCost { get; set; }
}

public sealed class ProcurementCreateViewModel
{
    /// <summary>Business order reference (DCX-…) or a raw order GUID; resolved to
    /// the order's id by the controller before the command is sent.</summary>
    public string? SourceOrderNumber { get; set; }
    public string? Notes { get; set; }
    public List<ProcurementCreateItemViewModel> Items { get; set; } = new();
}

/// <summary>Staff reply to a public contact-form submission; emailed to the enquirer.</summary>
public sealed class ReplyContactMessageFormViewModel
{
    [Required(ErrorMessage = "Subject is required.")]
    [StringLength(200, ErrorMessage = "Subject is too long.")]
    public string Subject { get; set; } = string.Empty;

    [Required(ErrorMessage = "Reply message is required.")]
    [StringLength(4000, ErrorMessage = "Reply message is too long.")]
    public string Body { get; set; } = string.Empty;
}

/// <summary>Visual campaign editor. BlocksJson is an internal form transport value, not an editable surface.</summary>
public sealed class MarketingBroadcastFormViewModel
{
    [Required(ErrorMessage = "A campaign name is required.")]
    [StringLength(120, ErrorMessage = "Campaign names can be up to 120 characters.")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "A subject is required.")]
    [StringLength(200, ErrorMessage = "Subjects can be up to 200 characters.")]
    public string Subject { get; set; } = string.Empty;

    [StringLength(200, ErrorMessage = "Preview lines can be up to 200 characters.")]
    public string? Preheader { get; set; }

    [Required(ErrorMessage = "A sender name is required.")]
    [StringLength(80, ErrorMessage = "Sender names can be up to 80 characters.")]
    public string SenderName { get; set; } = "DE-CHIMEX PRINT HUB";

    [EmailAddress(ErrorMessage = "Enter a valid reply-to email address.")]
    [StringLength(256)]
    public string? ReplyToEmail { get; set; }

    public DeChimex.Domain.Enums.MarketingAudienceSegment AudienceSegment { get; set; }

    [Required(ErrorMessage = "Add at least one email block.")]
    public string BlocksJson { get; set; } = string.Empty;
}

public sealed class MarketingConfirmFormViewModel
{
    public Guid BroadcastId { get; set; }
    public string SendMode { get; set; } = "now";
    public int ExpectedRecipientCount { get; set; }
    public bool Confirm { get; set; }
    public string? ScheduledAtUtc { get; set; }
}

public sealed class MarketingTestSendFormViewModel
{
    [Required(ErrorMessage = "Enter an email address for the test send.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    [StringLength(256)]
    public string TestEmail { get; set; } = string.Empty;
}

public sealed class MarketingRescheduleFormViewModel
{
    public string? ScheduledAtUtc { get; set; }
}
