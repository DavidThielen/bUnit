namespace Bunit.Rendering;

public class DeterministicRenderTests : TestContext
{
	[Fact]
	public async Task BlockAsyncRenderWhenEventDispatched()
	{
		var cut = Render<AsyncComponent>();

		cut.Find("button").Click();

		cut.Find("p").TextContent.ShouldBe("Rendering");

		// Set the task to finished
		cut.Instance.CompleteTask();
		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldBe("Rendered"));

		cut.Find("p").TextContent.ShouldBe("Rendered");
	}

	[Fact]
	public async Task BlockAsyncRenderWhenOnInitializedAsync()
	{
		var cut = Render<OnInitializedAsyncComponent>();

		cut.Find("p").TextContent.ShouldBe("False");

		cut.Instance.CompleteTask();
		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldBe("True"));
	}

	[Fact(Skip = "This test is flaky")]
	public async Task BlockTimerRendering()
	{
		var provider = new TriggerableTimeProvider();
		Services.AddSingleton<TimeProvider>(provider);
		var cut = Render<TimerComponent>();

		provider.TriggerTick();

		// The timer ticked but we are blocking rendering, so the component should not have been updated.
		cut.Find("p").TextContent.ShouldBe("0");

		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldNotBe("0"));

		// Now that we unblocked rendering, the component should have been updated.
		cut.Find("p").TextContent.ShouldBe("1");

		// Trigger the timer twice
		provider.TriggerTick();
		provider.TriggerTick();

		// Allow one pass through the render loop
		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldNotBe("1"));

		// The component should have been updated once!
		cut.Find("p").TextContent.ShouldBe("2");
	}

	[Fact]
	public async Task WaitForDoesNotRenderIfConditionIsInitiallyMet()
	{
		var provider = new TriggerableTimeProvider();
		Services.AddSingleton<TimeProvider>(provider);
		var cut = Render<TimerComponent>();
		provider.TriggerTick();

		// The condition is already met
		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldBe("0"));

		// No rerender has happened even though the timer ticked
		cut.Find("p").TextContent.ShouldBe("0");
	}

	[Fact]
	public void TaskCompletedLeadsToSynchronousRender()
	{
		var cut = Render<NoAwaitInitComponent>(
			p => p.Add(s => s.CreateTask, () => Task.CompletedTask));

		cut.Find("p").TextContent.ShouldBe("4");
	}

	[Fact]
	public void CompleteSynchronousCallWithStateHasChangedDoesntBlock()
	{
		var cut = Render<SyncOnInitComponent>();

		cut.Find("p").TextContent.ShouldBe("4");
	}

	[Fact]
	public async Task ContinueWith()
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cut = Render<NoAwaitInitComponent>(
			p => p.Add(
				s => s.CreateTask, () => tcs.Task.ContinueWith(_ => { }, TaskScheduler.Default)));

		cut.Find("p").TextContent.ShouldBe("0");

		tcs.SetResult();
		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldBe("1"));
	}

	[Fact]
	public async Task CallingInvokeAsyncWrappedInTaskRun()
	{
		var cut = Render<RenderOnDemandComponent>();
		_ = Task.Run(() => cut.Instance.Render(10));

		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldBe("10"));
	}

	[Fact]
	public async Task ConfigureAwaitComponent()
	{
		var cut = Render<AsyncConfigureAwaitComponent>();
		cut.Find("p").TextContent.ShouldBe("0");

		cut.Instance.Tcs.SetResult();
		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldNotBe("0"));

		cut.Instance.Tcs = new TaskCompletionSource();
		cut.Instance.Tcs.SetResult();
		
		// It is 4 as all renderer calls are batched together
		cut.Find("p").TextContent.ShouldBe("4");
	}

	[Fact]
	public async Task MyTestMethod2()
	{
		var tcs = new TaskCompletionSource<int>();
		var cut = Render<LoadingClickCounter>(ps => ps.Add(p => p.DataProvider, tcs.Task));

		tcs.SetResult(42);

		await cut.WaitForAssertionAsync(() => cut.Find("p").TextContent.ShouldBe("42"));

		cut.Find("button").Click();

		cut.Find("p").TextContent.ShouldBe("43");
	}

	[Fact]
	public void MyTestMethod2_x()
	{
		var tcs = new TaskCompletionSource<int>();
		var cut = Render<LoadingClickCounter>(ps => ps.Add(p => p.DataProvider, tcs.Task));
		tcs.SetResult(42);

		cut.Find("button").Click();

		cut.Find("p").TextContent.ShouldBe("43");
	}

	[Fact]
	public void MyTestMethod()
	{
		var cut = Render<LoadingClickCounter>(
			ps => ps.Add(
				p => p.DataProvider, 
				Task.Delay(10000).ContinueWith(x => 1, TaskScheduler.Current)));

		cut.Find("p").TextContent.ShouldBe("0");

		cut.Find("button").Click();

		cut.Find("p").TextContent.ShouldBe("1");
	}

	private sealed class RenderOnDemandComponent : IComponent
	{
		private RenderHandle renderHandle;
		public void Attach(RenderHandle renderHandle) => this.renderHandle = renderHandle;

		public Task SetParametersAsync(ParameterView parameters)
		{
			return Task.CompletedTask;
		}

		public Task Render(int number)
		{
			return renderHandle.Dispatcher.InvokeAsync(() =>
			{
				renderHandle.Render(builder =>
				{
					builder.OpenElement(0, "p");
					builder.AddContent(1, number);
					builder.CloseElement();
				});
			});
		}
	}

	private sealed class OnInitializedAsyncComponent : ComponentBase
	{
		private bool done;
		private readonly TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

		protected override async Task OnInitializedAsync()
		{
			done = false;
			await tcs.Task;
			done = true;
		}

		public void CompleteTask() => tcs.SetResult(true);

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenElement(0, "p");
			builder.AddContent(1, done);
			builder.CloseElement();
		}
	}

	private sealed class TimerComponent : ComponentBase, IDisposable
	{
		private int counter;
		private ITimer? timer;

		[Inject] private TimeProvider Provider { get; set; } = default!;

		protected override void OnParametersSet()
		{
			timer = Provider.CreateTimer(UpdateCounter, null, TimeSpan.Zero, TimeSpan.Zero);
		}

		private void UpdateCounter(object? state)
		{
			counter++;
			InvokeAsync(StateHasChanged);
		}
		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenElement(0, "p");
			builder.AddContent(1, counter);
			builder.CloseElement();
		}

		public void Dispose() => timer?.Dispose();
	}

	private sealed class TriggerableTimeProvider : TimeProvider
	{
		private ITimer? timer;

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			timer = base.CreateTimer(callback, state, TimeSpan.FromHours(2), TimeSpan.FromHours(2));
			return timer;
		}

		public void TriggerTick() => timer?.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
	}

	private sealed class SyncOnInitComponent : ComponentBase
	{
		private int someInt;
		protected override void OnInitialized()
		{
			for (var i = 0; i < 5; i++)
			{
				someInt = i;
				StateHasChanged();
			}
		}

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenElement(0, "p");
			builder.AddContent(1, someInt);
			builder.CloseElement();
		}
	}

	private sealed class NoAwaitInitComponent : ComponentBase
	{
		[Parameter, EditorRequired]
		public required Func<Task> CreateTask { get; set; }

		private int someInt;

		protected override async Task OnInitializedAsync()
		{
			for (var i = 0; i < 5; i++)
			{
				await CreateTask();
				someInt = i;
				StateHasChanged();
			}
		}

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenElement(0, "p");
			builder.AddContent(1, someInt);
			builder.CloseElement();
		}
	}

	private sealed class AsyncConfigureAwaitComponent : ComponentBase
	{
		public TaskCompletionSource Tcs { get; set; } = new();

		private int someInt;

		protected override async Task OnInitializedAsync()
		{
			for (var i = 0; i < 5; i++)
			{
				await Tcs.Task.ConfigureAwait(false);
				someInt = i;
				await InvokeAsync(StateHasChanged);
			}
		}

		protected override void BuildRenderTree(RenderTreeBuilder builder)
		{
			builder.OpenElement(0, "p");
			builder.AddContent(1, someInt);
			builder.CloseElement();
		}
	}
}
