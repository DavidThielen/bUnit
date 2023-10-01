using System.Runtime.CompilerServices;

namespace Bunit.Rendering;

internal class BunitRendererSynchronizationContext : SynchronizationContext
{
	private readonly object lockObject;
	private Task taskQueue;

	public event UnhandledExceptionEventHandler? UnhandledException;

	public BunitRendererSynchronizationContext() : this(new object(), Task.CompletedTask) { }

	private BunitRendererSynchronizationContext(object @lock, Task taskQueue)
	{
		lockObject = @lock;
		this.taskQueue = taskQueue;
	}

	/// <inheritdoc />
	public override SynchronizationContext CreateCopy() =>
		new BunitRendererSynchronizationContext(lockObject, taskQueue);

	// The following two Action/Func<TResult> overloads can be more optimized than their
	// async equivalents, as they don't need to deal with the possibility of the callback
	// posting back to this context.  As a result, they can use the Task for the InvokeAsync
	// operation as the object to use in the task queue itself if the operation is the next
	// in line.  For the async overloads, the callbacks might await and need to post back
	// to the current synchronization context, in which case their continuation could end
	// up seeing the InvokeAsync task as the antecedent, which would lead to deadlock. As
	// such, those operations must use a different task for the task queue. Note that this
	// requires these synchronous callbacks not doing sync-over-async with any work that
	// blocks waiting for this sync ctx to do work, but such cases are perilous, anyway,
	// as they invariably lead to deadlock.

	public Task InvokeAsync(Action action)
	{
		var completion = AsyncTaskMethodBuilder.Create();
		var t = completion.Task; // lazy initialize before passing around the struct

		lock (lockObject)
		{
			if (!taskQueue.IsCompleted)
			{
				taskQueue = PostAsync(taskQueue, Execute, (completion, action, this));
				return t;
			}

			taskQueue = t;
		}

		Execute((completion, action, this));
		return t;

		static void Execute((AsyncTaskMethodBuilder Completion, Action Action, BunitRendererSynchronizationContext Context) state)
		{
			var original = Current;
			SetSynchronizationContext(state.Context);
			try
			{
				state.Action();
				state.Completion.SetResult();
			}
			catch (Exception exception)
			{
				state.Completion.SetException(exception);
			}
			finally
			{
				SetSynchronizationContext(original);
			}
		}
	}

	public Task InvokeThenBlock(Action action, Task block)
	{
		var completion = AsyncTaskMethodBuilder.Create();
		var t = completion.Task; // lazy initialize before passing around the struct

		lock (lockObject)
		{
			if (!taskQueue.IsCompleted)
			{
				taskQueue = PostAsync(taskQueue, Execute, (completion, action, this)).ContinueWith(_ => block, TaskScheduler.Current);
				
				return t;
			}

			taskQueue = block;
		}

		Execute((completion, action, this));
		return t;

		static void Execute((AsyncTaskMethodBuilder Completion, Action Action, BunitRendererSynchronizationContext Context) state)
		{
			var original = Current;
			SetSynchronizationContext(state.Context);
			try
			{
				state.Action();
				state.Completion.SetResult();
			}
			catch (Exception exception)
			{
				state.Completion.SetException(exception);
			}
			finally
			{
				SetSynchronizationContext(original);
			}
		}
	}

	internal Task<TResult> InvokeThenBlock<TResult>(Func<TResult> function, Task block)
	{
		var completion = AsyncTaskMethodBuilder<TResult>.Create();
		var t = completion.Task; // lazy initialize before passing around the struct

		lock (lockObject)
		{
			if (!taskQueue.IsCompleted)
			{
				taskQueue = PostAsync(taskQueue, Execute, (completion, function, this)).ContinueWith(_ => block, TaskScheduler.Current);
				return t;
			}

			taskQueue = block;
		}

		Execute((completion, function, this));
		return t;

		static void Execute((AsyncTaskMethodBuilder<TResult> Completion, Func<TResult> Func, BunitRendererSynchronizationContext Context) state)
		{
			var original = Current;
			SetSynchronizationContext(state.Context);
			try
			{
				state.Completion.SetResult(state.Func());
			}
			catch (Exception exception)
			{
				state.Completion.SetException(exception);
			}
			finally
			{
				SetSynchronizationContext(original);
			}
		}
	}

	public Task<TResult> InvokeAsync<TResult>(Func<TResult> function)
	{
		var completion = AsyncTaskMethodBuilder<TResult>.Create();
		var t = completion.Task; // lazy initialize before passing around the struct

		lock (lockObject)
		{
			if (!taskQueue.IsCompleted)
			{
				taskQueue = PostAsync(taskQueue, Execute, (completion, function, this));
				return t;
			}

			taskQueue = t;
		}

		Execute((completion, function, this));
		return t;

		static void Execute((AsyncTaskMethodBuilder<TResult> Completion, Func<TResult> Func, BunitRendererSynchronizationContext Context) state)
		{
			var original = Current;
			SetSynchronizationContext(state.Context);
			try
			{
				state.Completion.SetResult(state.Func());
			}
			catch (Exception exception)
			{
				state.Completion.SetException(exception);
			}
			finally
			{
				SetSynchronizationContext(original);
			}
		}
	}

	public Task InvokeAsync(Func<Task> asyncAction)
	{
		var completion = AsyncTaskMethodBuilder.Create();
		var t = completion.Task; // lazy initialize before passing around the struct

		SendIfQuiescedOrElsePost(static async state =>
		{
			try
			{
				await state.asyncAction().ConfigureAwait(false);
				state.completion.SetResult();
			}
			catch (Exception exception)
			{
				state.completion.SetException(exception);
			}
		}, (completion, asyncAction));

		return t;
	}

	public Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> asyncFunction)
	{
		var completion = AsyncTaskMethodBuilder<TResult>.Create();
		var t = completion.Task; // lazy initialize before passing around the struct

		SendIfQuiescedOrElsePost(static async state =>
		{
			try
			{
				state.completion.SetResult(await state.asyncFunction().ConfigureAwait(false));
			}
			catch (Exception exception)
			{
				state.completion.SetException(exception);
			}
		}, (completion, asyncFunction));

		return t;
	}

	/// <inheritdoc/>
	public override void Post(SendOrPostCallback d, object? state)
	{
		lock (lockObject)
		{
			taskQueue = PostAsync(taskQueue, static s => s.d(s.state), (d, state));
		}
	}

	/// <inheritdoc/>
	public override void Send(SendOrPostCallback d, object? state)
	{
		Task antecedent;
		var completion = AsyncTaskMethodBuilder.Create();

		lock (lockObject)
		{
			antecedent = taskQueue;
			taskQueue = completion.Task;
		}

		// We have to block. That's the contract of Send - we don't expect this to be used
		// in many scenarios in Components.
		antecedent.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();

		InvokeWithThisAsCurrentSyncCtxThenSetResult(completion, d.Invoke, state); // Allocates, but using this method should be rare
	}

	/// <summary>
	/// Queues a work item that invokes the <paramref name="callback"/> with this instance as the current synchronization context.
	/// The work item will only run once <paramref name="antecedent"/> has completed.
	/// </summary>
	private async Task PostAsync<TState>(Task antecedent, Action<TState> callback, TState state)
	{
		await antecedent.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
		try
		{
			SetSynchronizationContext(this); // this will be undone automatically by the thread pool, so we don't need to here
			callback(state);
		}
		catch (Exception ex)
		{
			DispatchException(ex);
		}
	}

	/// <summary>Workhorse for the InvokeAsync methods.</summary>
	/// <remarks>
	/// Similar to Post, but it can run the work item synchronously if the context is not busy.
	/// This is the main code path used by components, we want to be able to run async work but only dispatch
	/// if necessary.
	/// </remarks>
	private void SendIfQuiescedOrElsePost<TState>(Action<TState> callback, TState state)
	{
		AsyncTaskMethodBuilder completion;
		lock (lockObject)
		{
			if (!taskQueue.IsCompleted)
			{
				taskQueue = PostAsync(taskQueue, callback, state);
				return;
			}

			// We can execute this synchronously because nothing is currently running or queued.
			completion = AsyncTaskMethodBuilder.Create();
			taskQueue = completion.Task;
		}

		InvokeWithThisAsCurrentSyncCtxThenSetResult(completion, callback, state);
	}

	/// <summary>
	/// Sets the current synchronization context to this instance, invokes the <paramref name="callback"/>,
	/// resets the synchronization context, and sets marks the builder as completed.
	/// </summary>
	private void InvokeWithThisAsCurrentSyncCtxThenSetResult<TState>(
		AsyncTaskMethodBuilder completion,
		Action<TState> callback,
		TState state)
	{
		var original = Current;
		try
		{
			SetSynchronizationContext(this);
			callback(state);
		}
		finally
		{
			SetSynchronizationContext(original);
			completion.SetResult();
		}
	}

	/// <summary>Invokes <see cref="UnhandledException"/> with the supplied exception instance.</summary>
	private void DispatchException(Exception ex) =>
		UnhandledException?.Invoke(this, new UnhandledExceptionEventArgs(ex, isTerminating: false));
}
