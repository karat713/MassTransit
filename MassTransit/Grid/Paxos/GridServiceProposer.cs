// Copyright 2007-2008 The Apache Software Foundation.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use 
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed 
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.Grid.Paxos
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using Internal;
	using Magnum.StateMachine;
	using Saga;
	using Sagas;

	public class GridServiceProposer : 
		Consumes<Promise>.For<Guid>,
		Consumes<PrepareRejected>.For<Guid>,
		Consumes<Accepted>.For<Guid>
	{
		private long _highestBallotId;
		private long _proposedBallotId;

		private IServiceBus _bus;
		private readonly IEndpointFactory _endpointFactory;
		private readonly ISagaRepository<GridServiceNode> _serviceNodeRepository;
		private IServiceBus _controlBus;
		private List<IEndpoint> _acceptors;
		private IdempotentList<Uri> _promised;
		private IdempotentList<Uri> _accepted;
		private Uri _proposedControlUri;
		private Uri _proposedDataUri;
		private int _rejectedPromiseCount;
		private long _highestRejectedBallotId;
		private Guid _serviceId;

		public GridServiceProposer(IServiceBus bus, IEndpointFactory endpointFactory, ISagaRepository<GridServiceNode> serviceNodeRepository)
		{
			_bus = bus;
			_endpointFactory = endpointFactory;
			_serviceNodeRepository = serviceNodeRepository;
			_controlBus = bus.ControlBus;
		}

		public void ProposeNextServiceNode(Type serviceType, Uri controlUri, Uri dataUri)
		{
			_serviceId = GridService.GenerateIdForType(serviceType);
			_proposedControlUri = controlUri;
			_proposedDataUri = dataUri;

			EnterPreparePhase();
		}

		public void Consume(Promise message)
		{
			if (message.BallotId != _proposedBallotId) return;

			_promised.Add(CurrentMessage.Headers.ResponseAddress);

			if(IsQuorumOfAcceptors(_promised.Count))
			{
				EnterAcceptPhase();
			}
		}

		private void EnterAcceptPhase()
		{
			Accept accept = new Accept
			{
				CorrelationId = _serviceId,
				BallotId = _highestBallotId,
				ControlUri = _proposedControlUri,
				DataUri = _proposedDataUri,
			};

			_promised
				.Select(x => _endpointFactory.GetEndpoint(x))
				.Each(x => x.Send(accept, context => context.SendResponseTo(_controlBus.Endpoint)));
		}

		private bool IsQuorumOfAcceptors(int count)
		{
			return count >= (_acceptors.Count / 2 + 1);
		}

		public Guid CorrelationId
		{
			get { return _serviceId; }
		}

		public void Consume(Accepted message)
		{
			if (message.BallotId != _proposedBallotId) return;

			_accepted.Add(CurrentMessage.Headers.ResponseAddress);
			if (_accepted.Count == _promised.Count)
			{
				Complete();
			}
		}

		private void Complete()
		{
		}

		public void Consume(PrepareRejected message)
		{
			if (message.BallotId != _proposedBallotId) return;

			_highestRejectedBallotId = Math.Max(_highestRejectedBallotId, message.ValueBallotId);

			int count = Interlocked.Increment(ref _rejectedPromiseCount);
			if(IsQuorumOfAcceptors(count))
			{
				EnterPreparePhase();
			}
		}

		private void EnterPreparePhase()
		{
			_proposedBallotId = _highestBallotId + 1;

			Prepare prepare = new Prepare
			{
				CorrelationId = _serviceId,
				BallotId = _proposedBallotId,
			};

			_rejectedPromiseCount = 0;
			_highestRejectedBallotId = _highestBallotId;

			_promised.Clear();

			_acceptors = _serviceNodeRepository.Where(x => x.ServiceId == _serviceId && x.CurrentState == GridServiceNode.Active)
				.Select(x => _endpointFactory.GetEndpoint(x.ControlUri))
				.ToList();

			_acceptors.Each(x => x.Send(prepare, context => context.SendResponseTo(_controlBus.Endpoint)));

		}
	}


	public class PreferredGridService :
		SagaStateMachine<PreferredGridService>,
		ISaga
	{
		static PreferredGridService()
		{
			Define(() =>
				{
				});
		}


		public static State Initial { get; set; }
		public static State Completed { get; set; }

		public Guid CorrelationId { get; set; }

		public IServiceBus Bus { get; set; }
	}
}