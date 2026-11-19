using System;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    [Serializable]
    public class MountSeat
    {
        public int seatIndex;
        public string seatName = "Driver";
        public Transform seatAnchor;
        public Transform entryApproachAnchor;
        public Transform[] exitCandidateAnchors;
        [Tooltip("Hide the rider visuals when seated. Disable for visible riders such as horses, motorcycles, or open turrets.")]
        public bool hideOccupantVisuals = true;

        [NonSerialized] private bool isOccupied;
        [NonSerialized] private GameObject occupant;
        [NonSerialized] private long reservationTransactionId;

        public bool IsOccupied => isOccupied;
        public GameObject Occupant => occupant;
        public long ReservationTransactionId => reservationTransactionId;

        public bool Reserve(GameObject prospectiveOccupant, long transactionId)
        {
            if (isOccupied) return false;
            if (reservationTransactionId != 0 && reservationTransactionId != transactionId) return false;
            reservationTransactionId = transactionId;
            occupant = prospectiveOccupant;
            return true;
        }

        public void ReleaseReservation(long transactionId)
        {
            if (reservationTransactionId == transactionId)
            {
                reservationTransactionId = 0;
                if (!isOccupied)
                {
                    occupant = null;
                }
            }
        }

        public void Occupy(GameObject newOccupant)
        {
            isOccupied = true;
            occupant = newOccupant;
            reservationTransactionId = 0;
        }

        public void Vacate()
        {
            isOccupied = false;
            occupant = null;
            reservationTransactionId = 0;
        }
    }
}
