using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using AssemblyCSharp;
using System.Linq;

[RequireComponent (typeof (Movement))]
[RequireComponent (typeof (BoundingShape))]
[RequireComponent (typeof (RangedWeapon))]
public class Unit : MonoBehaviour 
{
	public Terrain terrain;
	public PotentialFieldMapper potentialField;
	
	public Movement mov;
	public BoundingShape boundingShape;
	public RangedWeapon rangedWeapon;
	
	// the soldier object to spawn
	public GameObject soldierObject;
	
	private List<GameObject> soldiersInUnit;
	public GameObject[] GetSoldiers () {return soldiersInUnit.ToArray();}


	// the AI-determined target vector, which we use to set the direction vector
	public Vector3 target = new Vector3(0,0,0);

	public ArrayList enemies_in_range;
	
	
	// unit data
	public enum TEAM {A,B};
	public TEAM team;
	
	public int spawnSoldierNumber = 1;
	public int columnNumber = 2;
	
	
	public int currentSoldierNumber;
	
	// enemy counting
	int friendlies_close;
	int enemies_close;
	public float local_unit_detection_range = 5.0f;
	
	// mapping data
	private Vector3[] surroundings;
	
	private int local_values_num = 9;
	private float[] local_friend_influence_values;
	private float[] local_foe_influence_values;
	private float[] local_influences_values;
	private float[] local_tension_values;
	private float[] local_vulnerability_values;
	private float[] local_formation_vuln_values;
	private float[] local_influence_minus_self;
	private float[] terrain_values;
	
	private Node root;
	
	private float soldier_size_scale = 1.2f;
	
	void Start ()
	{
		rangedWeapon = soldierObject.GetComponent<RangedWeapon>();

		currentSoldierNumber = spawnSoldierNumber;
		// spawn soldiers of unit
		soldiersInUnit = new List<GameObject>();
		
		boundingShape.ModifyShape(BoundingShape.BOUNDING_SHAPE.RECTANGULAR, (spawnSoldierNumber/columnNumber -1)/2.0f * soldier_size_scale, (columnNumber - 1) /2.0f * soldier_size_scale);
		
		// spawn troops
		for(int i=0; i < spawnSoldierNumber; i++)
		{	
			Vector3 pos = SetSoldierLineFormation(i);
			// adjust position for height
			pos = new Vector3( pos.x, terrain.SampleHeight(pos), pos.z);
			
			GameObject soldier_obj = GameObject.Instantiate(soldierObject, pos, Quaternion.identity) as GameObject;
			Soldier s = soldier_obj.GetComponent<Soldier>();
			s.unit = this;
			
			Movement m = soldier_obj.GetComponent<Movement>();
			//m.moveSpeed = mov.moveSpeed;
			m.terrain = this.terrain;
			
			
			soldiersInUnit.Add(soldier_obj);
		}
		
	}
	
	void Awake () 
	{
		// initiliase arrays of local values (neighbours + own location)
		// arranged as follows: [TL,TM,TR,ML,MM,MR,LL,LM,LR]
		
		local_friend_influence_values 	= 	new float[local_values_num];
		local_foe_influence_values 		= 	new float[local_values_num];
		local_influences_values 		= 	new float[local_values_num];
		local_tension_values 			=	new float[local_values_num];
		local_vulnerability_values 		= 	new float[local_values_num];
		local_formation_vuln_values  	= 	new float[local_values_num];
		local_influence_minus_self 		=   new float[local_values_num];
		terrain_values 					= 	new float[local_values_num];

		enemies_in_range = new ArrayList();

		root = InitUnitAI();
	}
	
	void Update () 
	{
		
		GetLocalInfluences();
		root.Execute();
		LineFormation();
	}
	
	Node InitUnitAI ()
	{
		Leaf query_enemies_remain = new Leaf(new System.Func<bool>( () => {
			return 0 != CountUnitsOfTeam(GetOpposingTeam());
		}));
		
		Leaf stop_moving = new Leaf(new System.Func<bool>( () => {
			mov.moving = false;
			return true;
		}));
		
		Leaf query_locally_outnumbered = new Leaf(new System.Func<bool>( () => {
			CountUnitsClose();
			return enemies_close > friendlies_close;
		}));
		
		Leaf query_in_range = new Leaf(new System.Func<bool>( () => {
			enemies_in_range.Clear();

			Collider[] hitColliders = Physics.OverlapSphere(this.transform.position, rangedWeapon.range);

			foreach(Collider col in hitColliders)
			{
				if (col.gameObject.tag.Equals("Team_" + GetOpposingTeam().ToString()))
				{
					enemies_in_range.Add(col.gameObject.GetComponent<Unit>());
				}
			}
			//Debug.Log (enemies_in_range.Count + " enemies found to be in range!");
			return enemies_in_range.Count >= 1;
		}));

		Leaf signal_attack = new Leaf(new System.Func<bool>( () => {
			foreach(GameObject s_obj in soldiersInUnit)
			{
				Soldier s = s_obj.GetComponent<Soldier>();
				s.attacking_mode = true;
			}

			return true;
		}));

		Leaf signal_ceasefire = new Leaf(new System.Func<bool>( () => {
			foreach(GameObject s_obj in soldiersInUnit)
			{
				Soldier s = s_obj.GetComponent<Soldier>();
				s.attacking_mode = false;
			}
			
			return true;
		}));

		Leaf target_highest_influence = new Leaf(new System.Func<bool>( () => {
			target = surroundings[local_influence_minus_self.ToList().IndexOf(local_influence_minus_self.Max())];
			return true;
		}));
		
		Leaf target_highest_vulnerability_in_formation = new Leaf(new System.Func<bool>( () => {

			target = surroundings[local_formation_vuln_values.ToList().IndexOf(local_formation_vuln_values.Max())];
			return true;
		}));
		
		Leaf eliminate_collision_vectors = new Leaf(new System.Func<bool>( () => {
			RaycastHit hitInfo;
			if(Physics.Raycast(target, this.transform.position - target, out hitInfo, Vector3.Distance(target, this.transform.position)))
		   	{
				//Debug.Log (hitInfo.collider.gameObject.name);
				if(hitInfo.collider.gameObject.tag.Equals("Impassable"))
				{
					target = this.transform.position;
				}
				else
					if(hitInfo.collider.gameObject.tag.Equals("Team_A") || hitInfo.collider.gameObject.tag.Equals("Team_B"))
					{
						if(!collider.gameObject.GetComponent<Unit>().Equals(this))
							target = this.transform.position;
					}
			}

			return true;
		}));
		
		Leaf pathfind_around_obstacles = new Leaf(new System.Func<bool>( () => {
			PotentialFieldPathfind();	
			return true;
		}));
		

		/*
		Leaf adjust_speed_cohesion = new Leaf(new System.Func<bool>( () => {
			// change speed as a function of how much we are distancing ourselves from allies
			int index = surroundings.ToList().FindIndex(new System.Predicate<Vector3>( (Vector3 v) => {return v.Equals(target);}));
			int opposite_index = (surroundings.Length-1) - index;
			
			
			//float scale = local_influences_values[opposite_index] - local_influences_values[index];
			mov.ChangeSpeed(1 - 2 * scale);
			return true;
		}));
		*/
		
		Leaf set_direction = new Leaf(new System.Func<bool>( () => {
			mov.moving = true;
			mov.SetTarget(target);
			return true;
		}));
		
		SequenceNode no_enemies_tree = new SequenceNode(new List<Node> {
			new NotNode(query_enemies_remain),
			stop_moving
		});
		
		SequenceNode outnumbered_tree = new SequenceNode(new List<Node> {
			query_locally_outnumbered,
			target_highest_influence
		});
		
		SequenceNode not_outnumbered_tree = new SequenceNode(new List<Node> {
			target_highest_vulnerability_in_formation
		});
		
		SelectorNode target_setting_tree = new SelectorNode(new List<Node> {
			outnumbered_tree,
			not_outnumbered_tree
		});
		

		SequenceNode enemies_attack_tree = new SequenceNode(new List<Node> {
			query_in_range,
			signal_attack
		});

		SequenceNode enemies_movement_tree = new SequenceNode(new List<Node> { 
			signal_ceasefire,
			target_setting_tree,
			//adjust_speed_cohesion,
			eliminate_collision_vectors,
			pathfind_around_obstacles,
			set_direction
		});

		SelectorNode enemies_tree = new SelectorNode(new List<Node> {
			enemies_attack_tree,
			enemies_movement_tree
		});
		
		SelectorNode root = new SelectorNode(new List<Node> {
			no_enemies_tree,
			enemies_tree
		});
		
		
		return root;
	}
	
	/// <summary>
	/// Calculates the influence of all units on this unit's position and the 8 unit-length, manhattan distance
	/// positions away from it. Also transforms influence data into tension and vulnerability values through
	/// simple mathematical transformations (subtraction, addition).
	/// </summary>
	void GetLocalInfluences()
	{
		float distance = 1.0f;
		float distance_x = distance + boundingShape.shape_corner.x;
		float distance_y = distance + boundingShape.shape_corner.y;

		surroundings = new Vector3[]
		{
			new Vector3(transform.position.x - distance_x, 	transform.position.y, 	transform.position.z + distance_y),
			new Vector3(transform.position.x, 		transform.position.y, 	transform.position.z + distance_y),
			new Vector3(transform.position.x + distance_x, 	transform.position.y, 	transform.position.z + distance_y),
			new Vector3(transform.position.x - distance_x, 	transform.position.y, 	transform.position.z),
			this.transform.position,
			new Vector3(transform.position.x + distance_x, 	transform.position.y, 	transform.position.z),
			new Vector3(transform.position.x - distance_x, 	transform.position.y, 	transform.position.z - distance_y),
			new Vector3(transform.position.x, 		transform.position.y, 	transform.position.z - distance_y),
			new Vector3(transform.position.x + distance_x, 	transform.position.y, 	transform.position.z - distance_y),
		};
		
		for(int i=0; i < local_values_num; i++)
		{
			
			Vector3 pos = surroundings[i];
			//Debug.Log (surroundings[i]);
			local_friend_influence_values	[i] = InfluenceMapper.FriendInfluence(this, pos);
			//Debug.Log (local_friend_influence_values[i]);
			local_foe_influence_values		[i] = InfluenceMapper.FoeInfluence(this, pos);
			//Debug.Log (local_foe_influence_values[i]);
			local_influences_values			[i] = InfluenceMapper.TotalInfluence(local_friend_influence_values[i], local_foe_influence_values[i]);
			//Debug.Log (local_influences_values[i]);
			local_tension_values			[i] = InfluenceMapper.TotalTension(local_friend_influence_values[i], local_foe_influence_values[i]);
			//Debug.Log (local_tension_values[i]);
			local_vulnerability_values		[i] = InfluenceMapper.TotalVulnerability(local_tension_values[i], local_influences_values[i]);
			//Debug.Log (local_vulnerability_values[i]);
			local_formation_vuln_values 	[i] = InfluenceMapper.TotalFormationVulnerability(this, pos, local_vulnerability_values[i], local_tension_values[i]);
			
			local_influence_minus_self 		[i] = InfluenceMapper.TotalInfluenceMinusSelf(this, pos, local_influences_values[i]);
			
			terrain_values 					[i] = InfluenceMapper.TerrainValue(terrain, pos);
			
		}
		
	}
	
	/// <summary>
	/// Gets count of friends and foes within a certain
	/// spherical range (configurable in unity editor). 
	/// Useful to determined if outnumbered
	/// within a certain locality
	/// </summary>
	void CountUnitsClose()
	{
		int team_A_count = 0;
		int team_B_count = 0;
		
		Collider[] hitColliders = Physics.OverlapSphere(this.transform.position, local_unit_detection_range);
		
		foreach (Collider hit in hitColliders)
		{
			if(hit.gameObject.tag.Equals("Team_A"))
				team_A_count++;
			else
				if(hit.gameObject.tag.Equals("Team_B"))
					team_B_count++;
		}
		
		
		if(this.team.Equals(TEAM.A))
		{
			friendlies_close = team_A_count;
			enemies_close = team_B_count;
		}
		else
		{
			enemies_close = team_A_count;
			friendlies_close = team_B_count;
		}
	}
	
	int CountUnitsOfTeam(TEAM team)
	{
		GameObject[] units = GameObject.FindGameObjectsWithTag("Team_" + team.ToString());
		return units.Length;
	}
	
	TEAM GetOpposingTeam()
	{
		if(team.Equals(TEAM.A))
			return TEAM.B;
		else
			return TEAM.A;
	}
	
	void LineFormation()
	{
		boundingShape.ModifyShape(BoundingShape.BOUNDING_SHAPE.RECTANGULAR, (currentSoldierNumber/columnNumber -1)/2.0f * soldier_size_scale, (columnNumber - 1) /2.0f * soldier_size_scale);
	
		for(int i=0; i< soldiersInUnit.ToArray().Length; i++)
		{
			
			Movement m = soldiersInUnit.ToArray()[i].GetComponent<Movement>();
			m.SetTarget(SetSoldierLineFormation(i));
			m.moving = true;
		}
	}
	
	/// <summary>
	/// When a soldier dies, he signals his unit of his death, who then changes a few counters.
	/// After a certain number of deaths, he changes the formation, the indices of the other soldiers, etc.
	/// </summary>
	/// <param name="index">Index of soldier that died in the soldier list.</param>
	public void SignalDeath(GameObject s_obj)
	{
		Debug.Log("death signalled");

		currentSoldierNumber--;

		if(currentSoldierNumber == 0)
			GameObject.Destroy(this.gameObject);

		soldiersInUnit.Remove(s_obj);
	}
	
	Vector3 SetSoldierLineFormation(int index)
	{
		float angle = Vector3.Angle(new Vector3(0,0,1), this.transform.forward);
		
		Vector3 cross_product = Vector3.Cross(new Vector3(0,0,1), this.transform.forward);
		if (cross_product.y < 0) 
			angle = -angle;
		
		Vector3 angle_vec = new Vector3(0,angle,0);
		
		int number_soldiers_per_column = currentSoldierNumber / columnNumber;
		
		if(!Mathf.Approximately(number_soldiers_per_column, (float)(currentSoldierNumber) / columnNumber))
			number_soldiers_per_column++;
		
		float x_offset = boundingShape.GetPaddedCornerX() - index % (number_soldiers_per_column) * soldier_size_scale;
		float z_offset = boundingShape.GetPaddedCornerY() - index / (number_soldiers_per_column) * soldier_size_scale;
		
		float x_actual = x_offset + this.transform.position.x;
		float z_actual = z_offset + this.transform.position.z;
		
		Vector3 unrotated = new Vector3(x_actual, this.transform.position.y, z_actual);
		
		return RotatePointAroundPivot(unrotated, this.transform.position, angle_vec);
	}
	
	Vector3 RotatePointAroundPivot(Vector3 point, Vector3 pivot, Vector3 angle)
	{
		Vector3 dir = point - pivot;
		dir = Quaternion.Euler(angle) * dir;
		return dir + pivot;
	}
	
	void PotentialFieldPathfind()
	{	
		if(terrain.SampleHeight(target) < 1)
		{
			//Debug.Log ("Target is in river...");
			target = 2.0f * (target - this.transform.position) + this.transform.position;
			PotentialFieldPathfind();
		}
		else
		{
			float[] choices = new float[] 
				{
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[0].x, (int)surroundings[0].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[1].x, (int)surroundings[1].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[2].x, (int)surroundings[2].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[3].x, (int)surroundings[3].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[4].x, (int)surroundings[4].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[5].x, (int)surroundings[5].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[6].x, (int)surroundings[6].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[7].x, (int)surroundings[7].z],
					potentialField.potentialFieldValues[(int)target.x, (int)target.z][(int)surroundings[8].x, (int)surroundings[8].z]
				};
			
			
			// get rid of negative/0 values of impossibility
			for(int i=0; i< choices.Length; i++)
			{
				if(choices[i] < 0)
					choices[i] = float.MaxValue;
			}
			
			
			target = surroundings[choices.ToList().IndexOf(choices.Min())];
		
		}

	}
}
