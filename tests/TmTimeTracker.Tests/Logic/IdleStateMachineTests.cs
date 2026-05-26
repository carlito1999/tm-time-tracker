using FluentAssertions;
using TmTimeTracker.Logic;
using Xunit;

namespace TmTimeTracker.Tests.Logic;

public class IdleStateMachineTests
{
    private static IdleStateMachine New(int thresholdSeconds = 600) =>
        new(TimeSpan.FromSeconds(thresholdSeconds));

    [Fact]
    public void Starts_in_active_state()
    {
        New().Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Stays_active_below_threshold()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 100, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Becomes_idle_when_threshold_exceeded()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 700, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Lock_forces_immediate_idle_regardless_of_input_age()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 5, isLocked: true);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Returns_to_active_when_input_resumes()
    {
        var sm = New(600);
        sm.Observe(700, isLocked: false);
        sm.Observe(idleSeconds: 2, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Lock_release_alone_does_not_unlock_until_input()
    {
        var sm = New(600);
        sm.Observe(5, isLocked: true);
        sm.Observe(idleSeconds: 700, isLocked: false);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Claude_active_keeps_state_active_past_idle_threshold()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 9_999, isLocked: false, claudeActive: true);
        sm.Current.Should().Be(UserActivityState.Active);
    }

    [Fact]
    public void Claude_active_does_not_override_lock()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 5, isLocked: true, claudeActive: true);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Claude_inactive_with_idle_past_threshold_goes_idle()
    {
        var sm = New(600);
        sm.Observe(idleSeconds: 700, isLocked: false, claudeActive: false);
        sm.Current.Should().Be(UserActivityState.Idle);
    }

    [Fact]
    public void Emits_transition_on_change_only()
    {
        var sm = New(600);
        var transitions = new List<UserActivityState>();
        sm.OnTransition += s => transitions.Add(s);

        sm.Observe(100, false);
        sm.Observe(700, false);
        sm.Observe(800, false);
        sm.Observe(2, false);

        transitions.Should().Equal(UserActivityState.Idle, UserActivityState.Active);
    }
}
