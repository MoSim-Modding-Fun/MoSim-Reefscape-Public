using Games.Reefscape.Enums;
using Games.Reefscape.Robots;

namespace Prefabs.Reefscape.Robots.Mods.RoboGym._3950
{
    public class RoboGym : ReefscapeRobotBase
    {
        protected override void Start()
        {
            base.Start();
        }

        private void FixedUpdate()
        {
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    break;
                case ReefscapeSetpoints.Intake:
                    break;
                case ReefscapeSetpoints.Place:
                    break;
                case ReefscapeSetpoints.L1:
                    break;
                case ReefscapeSetpoints.Stack:
                    break;
                case ReefscapeSetpoints.L2:
                    break;
                case ReefscapeSetpoints.L3:
                    break;
                case ReefscapeSetpoints.L4:
                    break;
                case ReefscapeSetpoints.Climb:
                    break;
                case ReefscapeSetpoints.Climbed:
                    break;
            }
        }
    }
}