using DeChimex.Application.Common.Exceptions;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Notifications;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace DeChimex.UnitTests.Application.Features.Notifications;

/// <summary>
/// Unit tests for notification recipient scoping (2026-08-29 feature):
/// mark-read visibility guard and the persist-then-push behaviour of NotificationService.
/// </summary>
public class MarkNotificationReadHandlerTests
{
    private readonly INotificationRepository _notifications = Substitute.For<INotificationRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly MarkNotificationReadHandler _sut;

    public MarkNotificationReadHandlerTests() => _sut = new MarkNotificationReadHandler(_notifications, _uow);

    private static Notification CreateNotification(string? requiredPermission)
        => Notification.Create(NotificationType.LowStock, "Low stock alert", "msg", link: "/admin/inventory", requiredPermission: requiredPermission);

    [Fact]
    public async Task Handle_AnonymousUser_ThrowsNotFound_AndDoesNotTouchRepository()
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkNotificationReadCommand(id, null, []), CancellationToken.None));

        await _notifications.DidNotReceive().GetByIdAsync(id, Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MissingNotification_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((Notification?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkNotificationReadCommand(id, Guid.NewGuid(), []), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_LegacyBroadcast_NullPermission_MarksReadForAnyStaff()
    {
        var id = Guid.NewGuid();
        var n = CreateNotification(null);
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await _sut.Handle(new MarkNotificationReadCommand(id, Guid.NewGuid(), [], IsStaff: true), CancellationToken.None);

        n.IsRead.Should().BeTrue();
        _notifications.Received(1).Update(n);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserHoldsRequiredPermission_MarksRead()
    {
        var id = Guid.NewGuid();
        var n = CreateNotification("inventory.view");
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await _sut.Handle(new MarkNotificationReadCommand(id, Guid.NewGuid(), new[] { "inventory.view" }, IsStaff: true), CancellationToken.None);

        n.IsRead.Should().BeTrue();
        _notifications.Received(1).Update(n);
    }

    [Fact]
    public async Task Handle_LegacyBroadcast_NonStaff_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        var n = CreateNotification(null);
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkNotificationReadCommand(id, Guid.NewGuid(), new[] { "orders.create" }, IsStaff: false), CancellationToken.None));
        n.IsRead.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_PermissionScopedStaffRow_NonStaffHoldingThePermission_ThrowsNotFound()
    {
        var id = Guid.NewGuid();
        var n = CreateNotification("orders.create");
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkNotificationReadCommand(id, Guid.NewGuid(), new[] { "orders.create" }, IsStaff: false), CancellationToken.None));
    }

    [Fact]
    public async Task Handle_UserLacksRequiredPermission_ThrowsNotFound_AndDoesNotUpdate()
    {
        var id = Guid.NewGuid();
        var n = CreateNotification("inventory.view");
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkNotificationReadCommand(id, Guid.NewGuid(), new[] { "orders.view" }, IsStaff: true), CancellationToken.None));

        n.IsRead.Should().BeFalse();
        _notifications.DidNotReceive().Update(n);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyReadNotification_IsIdempotentAndPreservesReadTimestamp()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var n = Notification.Create(NotificationType.UserActivity, "Status update", "msg", recipientUserId: userId);
        n.MarkRead();
        var originalReadAt = n.ReadAtUtc;
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await _sut.Handle(new MarkNotificationReadCommand(id, userId, []), CancellationToken.None);

        n.IsRead.Should().BeTrue();
        n.ReadAtUtc.Should().Be(originalReadAt);
        _notifications.DidNotReceive().Update(n);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CustomerRecipient_MarksReadOnlyForThatRecipient()
    {
        var recipient = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = Guid.NewGuid();
        var n = Notification.Create(NotificationType.UserActivity, "Status update", "msg", recipientUserId: recipient);
        _notifications.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(n);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkNotificationReadCommand(id, other, new[] { "inventory.view" }), CancellationToken.None));

        n.IsRead.Should().BeFalse();

        await _sut.Handle(new MarkNotificationReadCommand(id, recipient, []), CancellationToken.None);
        n.IsRead.Should().BeTrue();
    }
}

public class MarkAllNotificationsReadHandlerTests
{
    private readonly INotificationRepository _notifications = Substitute.For<INotificationRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly MarkAllNotificationsReadHandler _sut;

    public MarkAllNotificationsReadHandlerTests() => _sut = new MarkAllNotificationsReadHandler(_notifications, _uow);

    [Fact]
    public async Task Handle_MarksVisibleUnreadRowsAndSavesOnce()
    {
        var userId = Guid.NewGuid();
        var first = Notification.Create(NotificationType.LowStock, "Stock", "msg", requiredPermission: "inventory.view");
        var second = Notification.Create(NotificationType.System, "Legacy", "msg");
        _notifications.GetUnreadForUserAsync(userId, Arg.Any<IReadOnlyCollection<string>>(), true, Arg.Any<CancellationToken>())
            .Returns(new List<Notification> { first, second });

        var changed = await _sut.Handle(
            new MarkAllNotificationsReadCommand(userId, new[] { "inventory.view" }, IsStaff: true), CancellationToken.None);

        changed.Should().Be(2);
        first.IsRead.Should().BeTrue();
        second.IsRead.Should().BeTrue();
        _notifications.Received(1).Update(first);
        _notifications.Received(1).Update(second);
        await _notifications.Received(1).GetUnreadForUserAsync(
            userId, Arg.Is<IReadOnlyCollection<string>>(p => p.Contains("inventory.view")), true, Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoUnreadRowsDoesNotWrite()
    {
        var userId = Guid.NewGuid();
        _notifications.GetUnreadForUserAsync(userId, Arg.Any<IReadOnlyCollection<string>>(), false, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Notification>());

        var changed = await _sut.Handle(new MarkAllNotificationsReadCommand(userId, [], IsStaff: false), CancellationToken.None);

        changed.Should().Be(0);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AnonymousCallerIsRejected()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.Handle(new MarkAllNotificationsReadCommand(null, [], IsStaff: false), CancellationToken.None));
        await _notifications.DidNotReceive().GetUnreadForUserAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}

public class NotificationServiceTests
{
    private readonly INotificationRepository _notifications = Substitute.For<INotificationRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly INotificationPusher _pusher = Substitute.For<INotificationPusher>();
    private readonly IWebPushDispatcher _webPush = Substitute.For<IWebPushDispatcher>();
    private readonly ILogger<NotificationService> _logger = Substitute.For<ILogger<NotificationService>>();
    private readonly NotificationService _sut;

    public NotificationServiceTests() => _sut = new NotificationService(_notifications, _uow, _pusher, _webPush, _logger);

    [Fact]
    public async Task NotifyAsync_PersistsRequiredPermission_AndPushesSamePayload()
    {
        Notification? captured = null;
        _notifications.AddAsync(Arg.Do<Notification>(x => captured = x), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.NotifyAsync(NotificationType.LowStock, "Low stock alert", "msg",
            link: "/admin/inventory", requiredPermission: "inventory.view");

        captured.Should().NotBeNull();
        captured!.RequiredPermission.Should().Be("inventory.view");
        captured.IsRead.Should().BeFalse();

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _pusher.Received(1).PushAsync(
            captured.Id, NotificationType.LowStock, "Low stock alert", "msg",
            "/admin/inventory", "inventory.view", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_LegacyCallWithoutPermission_PushesNullPermission()
    {
        await _sut.NotifyAsync(NotificationType.NewOrder, "New order placed", "msg", link: "/admin/orders/1");

        await _pusher.Received(1).PushAsync(
            Arg.Any<Guid>(), NotificationType.NewOrder, "New order placed", "msg",
            "/admin/orders/1", null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_DirectRecipientPushCarriesPersistedNotificationId()
    {
        Notification? captured = null;
        var recipient = Guid.NewGuid();
        _notifications.AddAsync(Arg.Do<Notification>(x => captured = x), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.NotifyAsync(NotificationType.DesignProofReady, "Proof ready", "msg", recipient,
            "/portal/orders/123/workspace");

        captured.Should().NotBeNull();
        await _webPush.Received(1).DispatchAsync(recipient, captured!.Id, "Proof ready", "msg",
            "/portal/orders/123/workspace", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_StaffBroadcastDoesNotSendWebPush()
    {
        await _sut.NotifyAsync(NotificationType.LowStock, "Stock", "msg", requiredPermission: "inventory.view");
        await _webPush.DidNotReceive().DispatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotifyAsync_PushFailure_DoesNotPropagate_AndPersistsAnyway()
    {
        _pusher.PushAsync(Arg.Any<Guid>(), Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub down")));

        var act = () => _sut.NotifyAsync(NotificationType.System, "sys", "msg");

        await act.Should().NotThrowAsync();
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
