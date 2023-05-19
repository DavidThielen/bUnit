using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components.Infrastructure;

namespace Bunit;

public abstract partial class TestContextBase
{
	/// <summary>
	/// Adds and returns a <see cref="BunitPersistentComponentState"/> to the services of this <see cref="TestContextBase"/>.
	/// </summary>
	/// <returns>The added <see cref="BunitPersistentComponentState"/>.</returns>
	public BunitPersistentComponentState AddBunitPersistentComponentState()
	{
		Services.AddSingleton<ComponentStatePersistenceManager>();
		Services.AddSingleton<PersistentComponentState>(s => s.GetRequiredService<ComponentStatePersistenceManager>().State);
		return new BunitPersistentComponentState(Services);
	}
}
