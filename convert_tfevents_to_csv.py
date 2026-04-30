import os
import csv
import glob
from tensorboard.backend.event_processing import event_accumulator

def extract_tfevents_to_csv(input_dir, output_dir):
    """
    Finds all tfevents files in the input_dir and converts their scalar data to CSV files in output_dir.
    """
    os.makedirs(output_dir, exist_ok=True)
    
    # Find all tfevents files recursively
    tfevents_files = glob.glob(os.path.join(input_dir, '**', 'events.out.tfevents.*'), recursive=True)
    
    if not tfevents_files:
        print(f"No tfevents files found in {input_dir}")
        return

    for file_path in tfevents_files:
        print(f"Processing: {file_path}")
        
        try:
            # size_guidance=0 means load all scalar events without truncation
            ea = event_accumulator.EventAccumulator(file_path, size_guidance={event_accumulator.SCALARS: 0})
            ea.Reload()
            
            tags = ea.Tags().get('scalars', [])
            if not tags:
                print(f"  No scalars found in {file_path}")
                continue
                
            # Try to extract the run name (the directory containing the 'logs' folder)
            # e.g. results/CarRacing_test/logs/events... -> CarRacing_test
            parts = os.path.normpath(file_path).split(os.sep)
            run_name = "unknown_run"
            for i, part in enumerate(parts):
                if part == 'logs' and i > 0:
                    run_name = parts[i-1]
                    break
            
            # Create a dedicated directory for each run in the output
            run_output_dir = os.path.join(output_dir, run_name)
            os.makedirs(run_output_dir, exist_ok=True)
            
            # Extract the timestamp or unique ID from the event file name to separate multiple runs
            event_filename = os.path.basename(file_path)
            parts_fn = event_filename.split('.')
            # typical format: events.out.tfevents.1775950955.ASUS...
            timestamp = parts_fn[3] if len(parts_fn) > 3 else "0"
            
            # We output one CSV per tag (scalar metric)
            for tag in tags:
                events = ea.Scalars(tag)
                
                # Make tag safe for filename (e.g. "Environment/Cumulative Reward" -> "Environment_Cumulative_Reward")
                safe_tag = tag.replace('/', '_').replace(' ', '_').replace(':', '_')
                
                csv_filename = f"{safe_tag}_{timestamp}.csv"
                csv_path = os.path.join(run_output_dir, csv_filename)
                
                with open(csv_path, 'w', newline='', encoding='utf-8') as f:
                    writer = csv.writer(f)
                    writer.writerow(['wall_time', 'step', 'value'])
                    for event in events:
                        writer.writerow([event.wall_time, event.step, event.value])
                        
            print(f"  Saved {len(tags)} scalar metrics to {run_output_dir}")
            
        except Exception as e:
            print(f"  Error processing {file_path}: {e}")

if __name__ == '__main__':
    # Determine directories based on the script's location
    base_dir = os.path.dirname(os.path.abspath(__file__))
    results_dir = os.path.join(base_dir, 'results')
    output_dir = os.path.join(results_dir, 'training_logs')
    
    print(f"Looking for tfevents files in: {results_dir}")
    print(f"Outputting CSVs to: {output_dir}")
    print("-" * 50)
    
    extract_tfevents_to_csv(results_dir, output_dir)
    print("-" * 50)
    print("Conversion complete!")
